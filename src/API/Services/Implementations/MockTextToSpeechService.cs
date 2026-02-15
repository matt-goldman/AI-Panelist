using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of text-to-speech service for testing
/// </summary>
public class MockTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<MockTextToSpeechService> _logger;
    private CancellationTokenSource? _speakCts;

    public bool IsSpeaking { get; private set; }

    public MockTextToSpeechService(ILogger<MockTextToSpeechService> logger)
    {
        _logger = logger;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock TTS: Speaking {Length} characters", text.Length);
        _speakCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            // Simulate TTS processing time (~100ms per word)
            var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var durationMs = wordCount * 100;

            _logger.LogDebug("Mock TTS: Simulating {Duration}ms of speech for {Words} words", durationMs, wordCount);

            await Task.Delay(durationMs, _speakCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Mock TTS: Speech cancelled");
        }
        finally
        {
            IsSpeaking = false;
            _speakCts?.Dispose();
            _speakCts = null;
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Mock TTS: Stopping speech");
        _speakCts?.Cancel();
        return Task.CompletedTask;
    }
}
