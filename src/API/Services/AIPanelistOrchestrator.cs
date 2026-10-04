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

            var transcript = _transcriptBuffer.GetFullTranscript();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogDebug("Skipping summary generation - transcript is empty");
                return;
            }

            _logger.LogInformation("Generating periodic summary");
            var summary = await _llmService.GenerateSummaryAsync(transcript);

            lock (_summaryLock)
            {
                _currentSummary = summary;
            }

            _logger.LogDebug("Summary generated: {Summary}", summary);

            await BroadcastConversationStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating summary");
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
            var response = await _llmService.GenerateResponseAsync(summary, recentTranscript, cancellationToken);
            _logger.LogInformation("Response generated: {Response}", response);

            // Capture the response asynchronously (non-blocking)
            _captureService.CaptureTextResponse(response, summary);
            _transcriptLog.Add(response, DateTime.UtcNow, PanelTranscriptLog.BubblesSpeaker);

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

        var tokens = _silenceGuard.Enabled
            ? watchdog.Watch(token => _llmService.StreamResponseAsync(summary, recentTranscript, token), cancellationToken)
            : _llmService.StreamResponseAsync(summary, recentTranscript, cancellationToken);

        var result = await _streamingSpeech.SpeakAsync(tokens, OnFirstAudio, cancellationToken);

        if (watchdog.Failure is not null || result.ChunkCount == 0)
        {
            _logger.LogError("No answer after {Chunks} chunk(s) spoken: {Reason}",
                result.ChunkCount, watchdog.Failure ?? "the stream produced nothing speakable");
            await SpeakFallbackAsync(OnFirstAudio, cancellationToken);
        }

        _logger.LogInformation("Response spoken in {Chunks} chunks: {Response}", result.ChunkCount, result.Text);
        _captureService.CaptureTextResponse(result.Text, summary);

        // Self-suppression keeps Bubbles' voice out of the transcript, so this is the only
        // way "what did you say earlier, Bubbles?" can be answered.
        _transcriptLog.Add(result.Text, DateTime.UtcNow, PanelTranscriptLog.BubblesSpeaker);

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
        _responseCts?.Dispose();
        _fillerCts?.Dispose();
        _stateLock?.Dispose();
    }
}
