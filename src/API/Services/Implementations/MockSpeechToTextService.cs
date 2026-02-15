using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of speech-to-text service for testing
/// </summary>
public class MockSpeechToTextService : ISpeechToTextService
{
    private readonly ILogger<MockSpeechToTextService> _logger;
    private CancellationTokenSource? _transcriptionCts;
    private bool _isPaused;

    public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

    public MockSpeechToTextService(ILogger<MockSpeechToTextService> logger)
    {
        _logger = logger;
    }

    public async Task StartTranscriptionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting mock transcription");
        _transcriptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Simulate continuous transcription with periodic mock text
        _ = Task.Run(async () =>
        {
            var mockPhrases = new[]
            {
                "This is a mock transcription.",
                "The system is working correctly.",
                "Continuous audio capture is simulated.",
                "This text would come from Whisper in production.",
                "Testing the rolling transcript buffer."
            };

            int index = 0;
            while (!_transcriptionCts.Token.IsCancellationRequested)
            {
                await Task.Delay(5000, _transcriptionCts.Token);

                if (!_isPaused)
                {
                    var text = mockPhrases[index % mockPhrases.Length];
                    index++;

                    TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                    {
                        Text = text,
                        Timestamp = DateTime.UtcNow,
                        IsFinal = true
                    });

                    _logger.LogDebug("Mock transcription: {Text}", text);
                }
            }
        }, _transcriptionCts.Token);
    }

    public Task StopTranscriptionAsync()
    {
        _logger.LogInformation("Stopping mock transcription");
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _transcriptionCts = null;
        return Task.CompletedTask;
    }

    public Task PauseTranscriptionAsync()
    {
        _logger.LogInformation("Pausing mock transcription");
        _isPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeTranscriptionAsync()
    {
        _logger.LogInformation("Resuming mock transcription");
        _isPaused = false;
        return Task.CompletedTask;
    }
}
