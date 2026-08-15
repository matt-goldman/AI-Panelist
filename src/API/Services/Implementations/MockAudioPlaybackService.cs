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

    public Task<IAudioPlaybackStream> OpenStreamAsync(
        AudioStreamFormat format,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock: Opening streaming playback ({Rate}Hz {Channels}ch)",
            format.SampleRate, format.Channels);

        IsPlaying = true;
        return Task.FromResult<IAudioPlaybackStream>(new MockPlaybackStream(this, format, _logger));
    }

    /// <summary>
    /// Simulates gapless playback by sleeping for the real duration of the PCM written,
    /// so streaming timings in logs are representative without any audio hardware.
    /// </summary>
    private sealed class MockPlaybackStream(
        MockAudioPlaybackService owner,
        AudioStreamFormat format,
        ILogger logger) : IAudioPlaybackStream
    {
        private readonly int _bytesPerSecond = format.SampleRate * format.Channels * (format.BitsPerSample / 8);
        private Task _playback = Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
        {
            var durationMs = (int)(pcm.Length * 1000L / Math.Max(1, _bytesPerSecond));
            logger.LogDebug("Mock: Queued {Bytes} bytes ({Duration}ms) of streamed audio", pcm.Length, durationMs);

            // Chain so queued chunks play back-to-back, as a real device would.
            _playback = _playback.ContinueWith(
                _ => Task.Delay(durationMs, cancellationToken),
                CancellationToken.None).Unwrap();

            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _playback;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Mock: Streaming playback cancelled");
            }
            finally
            {
                owner.IsPlaying = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            owner.IsPlaying = false;
            return ValueTask.CompletedTask;
        }
    }
}
