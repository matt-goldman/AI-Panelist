using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of audio playback service for testing
/// </summary>
public class MockAudioPlaybackService : IAudioPlaybackService
{
    private readonly ILogger<MockAudioPlaybackService> _logger;
    private CancellationTokenSource? _playCts;

    public bool IsPlaying { get; private set; }

    public MockAudioPlaybackService(ILogger<MockAudioPlaybackService> logger)
    {
        _logger = logger;
    }

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock: Playing audio from file {FilePath}", filePath);
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsPlaying = true;

        try
        {
            // Simulate audio playback (1 second for filler phrases)
            await Task.Delay(1000, _playCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Mock: Audio playback cancelled");
        }
        finally
        {
            IsPlaying = false;
            _playCts?.Dispose();
            _playCts = null;
        }
    }

    public async Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock: Playing audio from byte array ({Length} bytes)", audioData.Length);
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsPlaying = true;

        try
        {
            // Simulate audio playback based on data size
            var durationMs = Math.Max(1000, audioData.Length / 100);
            await Task.Delay(durationMs, _playCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Mock: Audio playback cancelled");
        }
        finally
        {
            IsPlaying = false;
            _playCts?.Dispose();
            _playCts = null;
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Mock: Stopping audio playback");
        _playCts?.Cancel();
        return Task.CompletedTask;
    }
}
