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
    private readonly AIPanelistOptions _options;

    private Timer? _summaryTimer;
    private string _currentSummary = string.Empty;
    private AiPanelistState _currentState = AiPanelistState.Idle;
    private bool _isDisabled = false;
    private CancellationTokenSource? _responseCts;
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
        IOptions<AIPanelistOptions> options)
    {
        _logger = logger;
        _hubContext = hubContext;
        _sttService = sttService;
        _llmService = llmService;
        _ttsService = ttsService;
        _audioPlayback = audioPlayback;
        _transcriptBuffer = transcriptBuffer;
        _options = options.Value;

        // Subscribe to transcription events
        _sttService.TranscriptionReceived += OnTranscriptionReceived;
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
    /// Cancel current response and return to Idle state
    /// </summary>
    public async Task CancelResponseAsync()
    {
        _logger.LogInformation("Cancelling AI response");

        _responseCts?.Cancel();
        await _ttsService.StopAsync();
        await _audioPlayback.StopAsync();

        await SetStateAsync(AiPanelistState.Idle);
        await BroadcastConversationStateAsync();
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

            // Broadcast updated transcript
            _ = BroadcastConversationStateAsync();
        }
    }

    private async void GenerateSummaryCallback(object? state)
    {
        try
        {
            var transcript = _transcriptBuffer.GetFullTranscript();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogDebug("Skipping summary generation - transcript is empty");
                return;
            }

            _logger.LogInformation("Generating periodic summary");
            _currentSummary = await _llmService.GenerateSummaryAsync(transcript);
            _logger.LogDebug("Summary generated: {Summary}", _currentSummary);

            await BroadcastConversationStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating summary");
        }
    }

    private async Task GenerateAndSpeakResponseAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Play filler phrase if enabled
            if (_options.EnableFillerPhrases && _options.FillerPhraseFiles.Any())
            {
                var fillerFile = _options.FillerPhraseFiles[_random.Next(_options.FillerPhraseFiles.Count)];
                _logger.LogInformation("Playing filler phrase: {File}", fillerFile);
                
                // Play filler phrase concurrently with thinking
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (File.Exists(fillerFile))
                        {
                            await _audioPlayback.PlayAsync(fillerFile, cancellationToken);
                        }
                        else
                        {
                            _logger.LogWarning("Filler file not found: {File}", fillerFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error playing filler phrase");
                    }
                }, cancellationToken);
            }

            // Set to Thinking state
            await SetStateAsync(AiPanelistState.Thinking);

            // Pause STT during response generation
            await _sttService.PauseTranscriptionAsync();

            // Get current context
            var summary = _currentSummary;
            var recentTranscript = _transcriptBuffer.GetRecentTranscript(TimeSpan.FromSeconds(60));

            _logger.LogInformation("Generating response...");
            var response = await _llmService.GenerateResponseAsync(summary, recentTranscript, cancellationToken);
            _logger.LogInformation("Response generated: {Response}", response);

            cancellationToken.ThrowIfCancellationRequested();

            // Speak the response - state changes to Speaking when audio playback actually starts
            _logger.LogInformation("Starting TTS synthesis...");
            await _ttsService.SpeakAsync(response, cancellationToken, async () =>
            {
                _logger.LogInformation("Audio ready, starting playback");
                await SetStateAsync(AiPanelistState.Speaking);
            });

            _logger.LogInformation("Response completed");

            // Return to Listening state
            await SetStateAsync(AiPanelistState.Listening);

            // Resume STT
            await _sttService.ResumeTranscriptionAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Response generation cancelled");
            await SetStateAsync(AiPanelistState.Listening);
            await _sttService.ResumeTranscriptionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating/speaking response");
            await SetStateAsync(AiPanelistState.Listening);
            await _sttService.ResumeTranscriptionAsync();
        }
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
        var state = new ConversationState(
            RollingTranscript: _transcriptBuffer.GetFullTranscript(),
            CurrentSummary: _currentSummary,
            IsSpeaking: _currentState == AiPanelistState.Speaking,
            IsDisabled: _isDisabled
        );

        await _hubContext.Clients.All.SendAsync(Messages.UpdateConversationState, state);
    }

    public void Dispose()
    {
        _summaryTimer?.Dispose();
        _responseCts?.Dispose();
        _stateLock?.Dispose();
    }
}
