using API.Configuration;
using API.Hubs;
using API.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Shared;

namespace API.Services;

/// <summary>
/// Orchestrates the AI panelist pipeline: STT → Transcript Buffer → Summary → LLM → TTS
/// </summary>
public class AIPanelistOrchestrator : IHostedService, IDisposable
{
    private readonly ILogger<AIPanelistOrchestrator> _logger;
    private readonly IHubContext<BubblesHub> _hubContext;
    private readonly ISpeechToTextService _sttService;
    private readonly ILanguageModelService _llmService;
    private readonly ITextToSpeechService _ttsService;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly TranscriptBufferService _transcriptBuffer;
    private readonly PanelTranscriptLog _transcriptLog;
    private readonly SummaryHealth _summaryHealth;
    private readonly TriggerPhraseMatcher _triggerMatcher;
    private readonly ResponseCaptureService _captureService;
    private readonly SelfSuppressionGate _suppressionGate;
    private readonly StreamingSpeechPipeline _streamingSpeech;
    private readonly StreamingResponseOptions _streamingOptions;
    private readonly FallbackSpeechService _fallbackSpeech;
    private readonly SilenceGuardOptions _silenceGuard;
    private readonly AIPanelistOptions _options;

    private Timer? _summaryTimer;
    private string _currentSummary = string.Empty;
    private readonly Lock _summaryLock = new();
    private AiPanelistState _currentState = AiPanelistState.Idle;
    private bool _isDisabled = false;
    private bool _isResponseInProgress = false;
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _fillerCts;

    /// <summary>
    /// The in-flight summary, so a trigger can cancel it. A stale summary is fine; a slow
    /// answer is not.
    /// </summary>
    private CancellationTokenSource? _summaryCts;

    /// <summary>When the last response finished, for the summarisation cooldown.</summary>
    private DateTime _responseEndedUtc = DateTime.MinValue;

    /// <summary>
    /// What the trigger listener heard when it fired.
    ///
    /// The listener recognises a phrase within about a second; the transcript worker is
    /// several seconds behind it by design, because it wants long windows for accuracy. So
    /// at the moment a response starts, the question that triggered it is often not in the
    /// rolling buffer yet — Bubbles answered "that's a bit of a cliffhanger" to a question
    /// it had not been given. The listener already transcribed those words, so use them.
    /// </summary>
    private string? _triggerUtterance;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly Random _random = new();

    public AIPanelistOrchestrator(
        ILogger<AIPanelistOrchestrator> logger,
        IHubContext<BubblesHub> hubContext,
        ISpeechToTextService sttService,
        ILanguageModelService llmService,
        ITextToSpeechService ttsService,
        IAudioPlaybackService audioPlayback,
        TranscriptBufferService transcriptBuffer,
        PanelTranscriptLog transcriptLog,
        SummaryHealth summaryHealth,
        TriggerPhraseMatcher triggerMatcher,
        ResponseCaptureService captureService,
        SelfSuppressionGate suppressionGate,
        StreamingSpeechPipeline streamingSpeech,
        IOptions<StreamingResponseOptions> streamingOptions,
        FallbackSpeechService fallbackSpeech,
        IOptions<SilenceGuardOptions> silenceGuard,
        IOptions<AIPanelistOptions> options)
    {
        _logger = logger;
        _hubContext = hubContext;
        _sttService = sttService;
        _llmService = llmService;
        _ttsService = ttsService;
        _audioPlayback = audioPlayback;
        _transcriptBuffer = transcriptBuffer;
        _transcriptLog = transcriptLog;
        _summaryHealth = summaryHealth;
        _triggerMatcher = triggerMatcher;
        _captureService = captureService;
        _suppressionGate = suppressionGate;
        _streamingSpeech = streamingSpeech;
        _streamingOptions = streamingOptions.Value;
        _fallbackSpeech = fallbackSpeech;
        _silenceGuard = silenceGuard.Value;
        _options = options.Value;

        // Subscribe to transcription events
        _sttService.TranscriptionReceived += OnTranscriptionReceived;

        // A spoken trigger is the same act as the moderator pressing the button, and goes
        // through the same guards (disabled, already answering).
        _triggerMatcher.TriggerDetected += OnTriggerPhraseDetected;
    }

    /// <summary>
    /// What Bubbles is doing, for anything that polls rather than listening on the hub —
    /// the setup app, which deliberately has no SignalR client so it needs no scripts from
    /// a CDN at a venue with no internet.
    /// </summary>
    public AiPanelistState CurrentState => _currentState;

    /// <summary>Whether the panelist has been switched off.</summary>
    public bool IsDisabled => _isDisabled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting AI Panelist Orchestrator");

        // Start continuous transcription
        await _sttService.StartTranscriptionAsync(cancellationToken);

        // Start periodic summary generation
        _summaryTimer = new Timer(
            GenerateSummaryCallback,
            null,
            TimeSpan.FromSeconds(_options.SummaryIntervalSeconds),
            TimeSpan.FromSeconds(_options.SummaryIntervalSeconds));

        // Get the fallback lines synthesised while nothing else needs the TTS. Not awaited:
        // a slow or absent TTS mustn't hold up startup.
        _ = Task.Run(() => _fallbackSpeech.WarmAsync(CancellationToken.None), CancellationToken.None);

        // Set initial state to Listening
        await SetStateAsync(AiPanelistState.Listening);

        _logger.LogInformation("AI Panelist Orchestrator started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping AI Panelist Orchestrator");

        _summaryTimer?.Dispose();
        await _sttService.StopTranscriptionAsync();
        _responseCts?.Cancel();

        _logger.LogInformation("AI Panelist Orchestrator stopped");
    }

    /// <summary>
    /// Trigger AI response generation and playback
    /// </summary>
    public async Task TriggerResponseAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_isDisabled)
            {
                _logger.LogWarning("Cannot trigger response - panelist is disabled");
                return;
            }

            if (_currentState == AiPanelistState.Thinking || _currentState == AiPanelistState.Speaking)
            {
                _logger.LogWarning("Cannot trigger response - already processing");
                return;
            }

            _logger.LogInformation("Triggering AI response");

            // The response always wins. An uninterruptible summary is the biggest source
            // of time-to-first-audio we can actually control: one model, one GPU, and a
            // summary mid-flight owns it until it finishes.
            if (_summaryCts is { IsCancellationRequested: false })
            {
                _logger.LogInformation("Cancelling the in-flight summary so the response has the model to itself");
                _summaryCts.Cancel();
            }

            // Cancel any previous response
            _responseCts?.Cancel();
            _responseCts = new CancellationTokenSource();
            var cancellationToken = _responseCts.Token;

            // Start response generation workflow
            _ = Task.Run(async () => await GenerateAndSpeakResponseAsync(cancellationToken), cancellationToken);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Plays a canned message to introduce the AI panelist (can be used when moderator first enables the panelist or for testing connectivity)
    /// </summary>
    /// <returns></returns>
    public async Task IntroduceSelf()
    {
        _logger.LogInformation("Introducing self");

        await _stateLock.WaitAsync();

        try
        {
            if (_isDisabled)
            {
                _logger.LogWarning("Cannot introduce self - panelist is disabled");
                return;
            }

            // use an audio file called Intro.wav, same as how the thinkng filler phrases are handled, but without the random selection
            try
            {
                if (File.Exists(_options.IntroPhrase))
                {
                    using var speaking = _suppressionGate.Suppress("intro");
                    await SetStateAsync(AiPanelistState.Speaking);
                    await _audioPlayback.PlayAsync(_options.IntroPhrase, CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning("Intro file not found: {IntroPhrase}", _options.IntroPhrase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error playing intro audio");
            }

            // Return to Listening state after introduction
            await SetStateAsync(AiPanelistState.Idle);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Speak a fixed line through the whole response path — TTS, chunking, playback,
    /// envelope publishing, self-suppression — with only the model left out.
    ///
    /// This is the end-to-end check that cannot be done by inspecting anything: if the
    /// line comes out of the right sink and the mouth moves on the display, the parts
    /// between them are all working. It is the one thing the readiness checks cannot
    /// prove on their own.
    /// </summary>
    /// <returns>Why it couldn't be spoken, or null if it was.</returns>
    public async Task<string?> SpeakTestLineAsync(string text, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_isDisabled) return "The panelist is disabled.";
            if (_currentState is AiPanelistState.Thinking or AiPanelistState.Speaking) return "Already speaking.";

            _responseCts?.Cancel();
            _responseCts = new CancellationTokenSource();
        }
        finally
        {
            _stateLock.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_responseCts.Token, cancellationToken);
        IDisposable? speaking = null;
        _isResponseInProgress = true;

        try
        {
            _logger.LogInformation("Speaking test line: {Text}", text);

            var result = await _streamingSpeech.SpeakAsync(
                Single(text),
                () =>
                {
                    speaking = _suppressionGate.Suppress("test line");
                    return SetStateAsync(AiPanelistState.Speaking);
                },
                linked.Token);

            await SetStateAsync(AiPanelistState.Listening);

            return result.ChunkCount > 0 ? null : "The text to speech produced no audio.";
        }
        catch (OperationCanceledException)
        {
            await SetStateAsync(AiPanelistState.Listening);
            return "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test line failed");
            await SetStateAsync(AiPanelistState.Listening);
            return ex.Message;
        }
        finally
        {
            speaking?.Dispose();
            _isResponseInProgress = false;
            _responseEndedUtc = DateTime.UtcNow;
        }

        static async IAsyncEnumerable<string> Single(string text)
        {
            yield return text;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Cancel current response and return to Idle state
    /// </summary>
    public async Task CancelResponseAsync()
    {
        _logger.LogInformation("Cancelling AI response");

        await _stateLock.WaitAsync();
        try
        {
            _responseCts?.Cancel();
            _fillerCts?.Cancel();
            await _ttsService.StopAsync();
            await _audioPlayback.StopAsync();

            _isResponseInProgress = false;
            await SetStateAsync(AiPanelistState.Idle);
            await BroadcastConversationStateAsync();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Disable the AI panelist
    /// </summary>
    public async Task DisableAsync()
    {
        _logger.LogInformation("Disabling AI panelist");

        await _stateLock.WaitAsync();
        try
        {
            _isDisabled = true;
            _responseCts?.Cancel();
            await _ttsService.StopAsync();
            await _audioPlayback.StopAsync();
            await _sttService.StopTranscriptionAsync();

            await SetStateAsync(AiPanelistState.Idle);
            await BroadcastConversationStateAsync();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Re-enable the AI panelist
    /// </summary>
    public async Task EnableAsync()
    {
        _logger.LogInformation("Enabling AI panelist");

        await _stateLock.WaitAsync();
        try
        {
            _isDisabled = false;
            await _sttService.StartTranscriptionAsync();
            await SetStateAsync(AiPanelistState.Listening);
            await BroadcastConversationStateAsync();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void OnTranscriptionReceived(object? sender, TranscriptionReceivedEventArgs e)
    {
        if (e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
        {
            _logger.LogDebug("Transcription received from {Speaker}: {Text}", e.SpeakerName ?? "Unknown", e.Text);
            _transcriptBuffer.AddEntry(e.Text, e.Timestamp, e.SpeakerName);
            _transcriptLog.Add(e.Text, e.Timestamp, e.SpeakerName);

            // Broadcast updated transcript
            _ = BroadcastConversationStateAsync();
        }
    }

    private async void OnTriggerPhraseDetected(object? sender, TriggerPhraseDetectedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Spoken trigger from {Source}, triggering response", e.Source ?? "unknown");
            _triggerUtterance = e.Text;
            await TriggerResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling spoken trigger");
        }
    }

    private async void GenerateSummaryCallback(object? state)
    {
        try
        {
            // Skip summary generation if response is in progress
            if (_isResponseInProgress)
            {
                _logger.LogDebug("Skipping summary generation - response in progress");
                return;
            }

            // And for a while afterwards: the question that follows an answer tends to
            // arrive while the answer is still being heard.
            var sinceResponse = DateTime.UtcNow - _responseEndedUtc;
            if (sinceResponse < TimeSpan.FromSeconds(_options.SummaryCooldownSeconds))
            {
                _logger.LogDebug("Skipping summary generation - only {Seconds:F0}s since the last response",
                    sinceResponse.TotalSeconds);
                return;
            }

            var transcript = _transcriptBuffer.GetFullTranscript();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogDebug("Skipping summary generation - transcript is empty");
                return;
            }

            var cts = new CancellationTokenSource();
            _summaryCts = cts;

            try
            {
                _logger.LogInformation("Generating periodic summary");
                var summary = await _llmService.GenerateSummaryAsync(transcript, cts.Token);

                // An empty result must never replace a good summary. A reasoning model that
                // spends its whole budget thinking returns nothing, and overwriting with
                // that silently strips every later response of its context - which is
                // exactly what happened: nine "successful" summaries and responses still
                // going out with none.
                if (string.IsNullOrWhiteSpace(summary))
                {
                    _summaryHealth.RecordFailure("the model returned an empty summary");
                    _logger.LogWarning(
                        "Summary came back empty and has been discarded; keeping the previous one. "
                        + "If this persists the model is spending its whole budget reasoning - raise the "
                        + "summary token budget or turn its thinking off.");
                    return;
                }

                lock (_summaryLock)
                {
                    _currentSummary = summary;
                }

                _logger.LogDebug("Summary generated: {Summary}", summary);
                _summaryHealth.RecordSuccess();

                await BroadcastConversationStateAsync();
            }
            finally
            {
                if (ReferenceEquals(_summaryCts, cts)) _summaryCts = null;
                cts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Abandoned for a response. The previous summary stays current, which is the
            // whole point of the rolling-summary design.
            _summaryHealth.RecordAbandoned();
            _logger.LogInformation("Summary abandoned so a response could start");
        }
        catch (Exception ex)
        {
            // Recorded as well as logged: a summary endpoint that is wrong, unreachable or
            // unauthorised otherwise fails in silence, and every response afterwards is
            // generated with no summary at all.
            _summaryHealth.RecordFailure($"{ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Error generating summary - responses will have no discussion summary until this works");
        }
    }

    private async Task GenerateAndSpeakResponseAsync(CancellationToken cancellationToken)
    {
        _isResponseInProgress = true;

        // Opened the moment audio actually starts leaving the app, not now: the hosts may
        // still be talking while we think, and that is transcript we want to keep.
        IDisposable? speaking = null;

        // Time-to-first-audio is the metric that matters, not total generation time.
        // Logged on both paths so the streamed and non-streamed behaviour are comparable.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (_streamingOptions.Enabled)
            {
                await StreamResponseAsync(cancellationToken, scope => speaking = scope);
                return;
            }

            // Play filler phrase if enabled (with separate cancellation so we can interrupt it)
            _fillerCts?.Cancel();
            _fillerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fillerToken = _fillerCts.Token;

            if (_options.EnableFillerPhrases && _options.FillerPhraseFiles.Any())
            {
                var fillerFile = _options.FillerPhraseFiles[_random.Next(_options.FillerPhraseFiles.Count)];
                _logger.LogInformation("Playing filler phrase: {File}", fillerFile);

                // Play filler phrase concurrently with thinking (will be interrupted when TTS starts)
                _ = Task.Run(async () =>
                {
                    // A filler phrase is still Bubbles' voice going out over the stream,
                    // so it needs the same suppression as a real response.
                    using var fillerSuppression = _suppressionGate.Suppress("filler phrase");
                    try
                    {
                        if (File.Exists(fillerFile))
                        {
                            await _audioPlayback.PlayAsync(fillerFile, fillerToken);
                        }
                        else
                        {
                            _logger.LogWarning("Filler file not found: {File}", fillerFile);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Filler phrase interrupted for TTS playback");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error playing filler phrase");
                    }
                }, fillerToken);
            }

            // Set to Thinking state
            await SetStateAsync(AiPanelistState.Thinking);

            // Get current context with synchronized access
            string summary;
            lock (_summaryLock)
            {
                summary = _currentSummary;
            }
            var recentTranscript = _transcriptBuffer.GetRecentTranscript(TimeSpan.FromSeconds(60));

            _logger.LogInformation("Generating response...");
            var response = await _llmService.GenerateResponseAsync(
                summary, recentTranscript, GetQuestionSinceLastResponse(), cancellationToken);
            _logger.LogInformation("Response generated: {Response}", response);

            // Capture the response asynchronously (non-blocking)
            _captureService.CaptureTextResponse(response, summary);
            RecordOwnTurn(response);

            cancellationToken.ThrowIfCancellationRequested();

            // Stop filler phrase before TTS playback starts
            _fillerCts?.Cancel();
            await _audioPlayback.StopAsync();

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogError("Response was empty");
                await SpeakFallbackAsync(() =>
                {
                    speaking = _suppressionGate.Suppress("fallback line");
                    return SetStateAsync(AiPanelistState.Speaking);
                }, cancellationToken);
                await SetStateAsync(AiPanelistState.Listening);
                return;
            }

            // Speak the response - state changes to Speaking when audio playback actually starts
            _logger.LogInformation("Starting TTS synthesis...");
            await _ttsService.SpeakAsync(response, cancellationToken, () =>
            {
                _logger.LogInformation("Time to first audio: {Ms}ms (non-streamed)",
                    (int)stopwatch.Elapsed.TotalMilliseconds);
                speaking = _suppressionGate.Suppress("tts playback");
                return SetStateAsync(AiPanelistState.Speaking);
            });

            _logger.LogInformation("Response completed");

            // Return to Listening state
            await SetStateAsync(AiPanelistState.Listening);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Response generation cancelled");
            await SetStateAsync(AiPanelistState.Listening);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating/speaking response");

            // Whatever broke, someone asked a question. Recorded or cached audio doesn't
            // need the TTS, so this usually works even when the TTS is what failed.
            try
            {
                await SpeakFallbackAsync(() =>
                {
                    speaking ??= _suppressionGate.Suppress("fallback line");
                    return SetStateAsync(AiPanelistState.Speaking);
                }, cancellationToken);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback line failed too");
            }

            await SetStateAsync(AiPanelistState.Listening);
        }
        finally
        {
            // Closing the scope starts the tail: transcripts stay suppressed for a further
            // few hundred milliseconds while our audio is still in flight through
            // StreamYard and back into the captured tab mix.
            speaking?.Dispose();
            _isResponseInProgress = false;
            _responseEndedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// The streamed path: tokens are cut into clauses and spoken as they arrive, so the
    /// first sound lands while the model is still writing.
    ///
    /// There are no filler phrases here, deliberately. They existed to cover the gap
    /// between the trigger and the answer; the first real clause now fills that gap
    /// coherently, and a canned "Great question!" would collide with it.
    /// </summary>
    private async Task StreamResponseAsync(CancellationToken cancellationToken, Action<IDisposable> onSpeakingScope)
    {
        await SetStateAsync(AiPanelistState.Thinking);

        // A consistent snapshot: the summary timer must not swap the summary out from under
        // a response that has already read half of it.
        string summary;
        lock (_summaryLock)
        {
            summary = _currentSummary;
        }
        var recentTranscript = _transcriptBuffer.GetRecentTranscript(TimeSpan.FromSeconds(60));

        _logger.LogInformation("Streaming response...");

        // Opened once, whether the first sound is the answer, a holding line or a
        // fallback - a second scope would leave suppression open for good.
        var speakingOpened = false;
        Task OnFirstAudio()
        {
            if (speakingOpened) return Task.CompletedTask;
            speakingOpened = true;

            _logger.LogInformation("First audio, starting playback");
            onSpeakingScope(_suppressionGate.Suppress("streamed tts playback"));
            return SetStateAsync(AiPanelistState.Speaking);
        }

        // Turns "the model never answered" into a clean end of stream rather than an
        // exception, so whatever was already queued still plays before the fallback line.
        using var watchdog = new AnswerWatchdog(
            _silenceGuard.Enabled ? TimeSpan.FromSeconds(_silenceGuard.MaxSilenceSeconds) : TimeSpan.Zero,
            _logger);

        var question = GetQuestionSinceLastResponse();
        _logger.LogDebug("Responding to: {Question}", question);

        var tokens = _silenceGuard.Enabled
            ? watchdog.Watch(token => _llmService.StreamResponseAsync(summary, recentTranscript, question, token), cancellationToken)
            : _llmService.StreamResponseAsync(summary, recentTranscript, question, cancellationToken);

        var result = await _streamingSpeech.SpeakAsync(tokens, OnFirstAudio, cancellationToken);

        if (watchdog.Failure is not null || result.ChunkCount == 0)
        {
            _logger.LogError("No answer after {Chunks} chunk(s) spoken: {Reason}",
                result.ChunkCount, watchdog.Failure ?? "the stream produced nothing speakable");
            await SpeakFallbackAsync(OnFirstAudio, cancellationToken);
        }

        _logger.LogInformation("Response spoken in {Chunks} chunks: {Response}", result.ChunkCount, result.Text);
        _captureService.CaptureTextResponse(result.Text, summary);

        RecordOwnTurn(result.Text);

        await SetStateAsync(AiPanelistState.Listening);
    }

    /// <summary>
    /// Say a canned line in place of an answer that never came. Silence is the one
    /// unacceptable outcome: on stage it reads as Bubbles ignoring whoever asked.
    /// </summary>
    private async Task SpeakFallbackAsync(Func<Task> onFirstAudio, CancellationToken cancellationToken)
    {
        if (!_silenceGuard.Enabled)
        {
            _logger.LogWarning("Silence guard is off - saying nothing");
            return;
        }

        await _fallbackSpeech.SpeakAsync(onFirstAudio, cancellationToken);
    }

    /// <summary>
    /// Put Bubbles' own answer into the record.
    ///
    /// Into the rolling buffer as well as the session log, because self-suppression keeps
    /// its voice out of the captured audio — so without this the model cannot see its own
    /// contributions, cannot tell which questions it has already answered, and repeats itself.
    /// </summary>
    private void RecordOwnTurn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var now = DateTime.UtcNow;
        _transcriptBuffer.AddEntry(text, now, PanelTranscriptLog.BubblesSpeaker);
        _transcriptLog.Add(text, now, PanelTranscriptLog.BubblesSpeaker);
    }

    /// <summary>
    /// Everything said since Bubbles last finished speaking — the thing it is actually
    /// being asked now.
    ///
    /// The rolling window alone was not enough: a minute of transcript can hold three
    /// questions, Bubbles' own replies are kept out of it by self-suppression, and so the
    /// model had no way to tell which it had already answered. It answered the previous
    /// one while the new one sat two lines below.
    /// </summary>
    private string GetQuestionSinceLastResponse()
    {
        var since = _responseEndedUtc == DateTime.MinValue
            ? TimeSpan.FromSeconds(30)
            : DateTime.UtcNow - _responseEndedUtc;

        // Floored so a very fast follow-up still carries its question; capped so a long
        // gap doesn't just reproduce the whole window.
        var window = TimeSpan.FromSeconds(Math.Clamp(since.TotalSeconds, 8, 45));
        var transcribed = _transcriptBuffer.GetRecentTranscript(window);

        // The trigger listener runs several seconds ahead of the transcript, so what it
        // heard is usually the newest thing said - and often the only copy of the question.
        var utterance = Interlocked.Exchange(ref _triggerUtterance, null);
        if (string.IsNullOrWhiteSpace(utterance)) return transcribed;

        // Only add it if the transcript has not caught up with it already, or the question
        // appears twice and the model answers it twice.
        var normalisedTranscript = TriggerPhraseMatcher.Normalise(transcribed);
        var normalisedUtterance = TriggerPhraseMatcher.Normalise(utterance);

        if (normalisedUtterance.Length > 0 && normalisedTranscript.Contains(normalisedUtterance, StringComparison.Ordinal))
        {
            return transcribed;
        }

        _logger.LogDebug("Transcript had not caught up with the trigger; using what the listener heard: {Text}", utterance);

        return string.IsNullOrWhiteSpace(transcribed)
            ? utterance
            : $"{transcribed}\n{utterance}";
    }

    private async Task SetStateAsync(AiPanelistState newState)
    {
        if (_currentState == newState) return;

        _currentState = newState;
        _logger.LogInformation("State changed to: {State}", newState);

        await _hubContext.Clients.All.SendAsync(Messages.UpdatePanelState, newState);
    }

    private async Task BroadcastConversationStateAsync()
    {
        string summary;
        lock (_summaryLock)
        {
            summary = _currentSummary;
        }

        var state = new ConversationState(
            RollingTranscript: _transcriptBuffer.GetFullTranscript(),
            CurrentSummary: summary,
            IsSpeaking: _currentState == AiPanelistState.Speaking,
            IsDisabled: _isDisabled
        );

        await _hubContext.Clients.All.SendAsync(Messages.UpdateConversationState, state);
    }

    public void Dispose()
    {
        _triggerMatcher.TriggerDetected -= OnTriggerPhraseDetected;
        _summaryTimer?.Dispose();
        _summaryCts?.Dispose();
        _responseCts?.Dispose();
        _fillerCts?.Dispose();
        _stateLock?.Dispose();
    }
}
