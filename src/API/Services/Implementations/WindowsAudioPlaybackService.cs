using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

/// <summary>
/// Windows audio playback service using NAudio
/// </summary>
public class WindowsAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly ILogger<WindowsAudioPlaybackService> _logger;
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFileReader;
    private CancellationTokenSource? _cts;

    public bool IsPlaying { get; private set; }

    public WindowsAudioPlaybackService(ILogger<WindowsAudioPlaybackService> logger)
    {
        _logger = logger;
    }

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("Audio file not found: {FilePath}", filePath);
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        _logger.LogInformation("NAudio: Playing audio from {FilePath}", filePath);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _audioFileReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFileReader);

            var tcs = new TaskCompletionSource<bool>();
            IsPlaying = true;

            _waveOut.PlaybackStopped += (s, e) =>
            {
                IsPlaying = false;
                if (e.Exception != null)
                {
                    _logger.LogError(e.Exception, "Playback stopped with error");
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            };

            _waveOut.Play();

            // Wait for playback to complete or cancellation
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
            
            _logger.LogInformation("NAudio: Playback completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NAudio: Playback cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NAudio: Error during playback");
            throw;
        }
        finally
        {
            Cleanup();
        }
    }

    public async Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("NAudio: Playing audio from byte array ({Length} bytes)", audioData.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            using var ms = new MemoryStream(audioData);
            using var reader = new WaveFileReader(ms);
            
            _waveOut = new WaveOutEvent();
            _waveOut.Init(reader);

            var tcs = new TaskCompletionSource<bool>();
            IsPlaying = true;

            _waveOut.PlaybackStopped += (s, e) =>
            {
                IsPlaying = false;
                if (e.Exception != null)
                {
                    _logger.LogError(e.Exception, "Playback stopped with error");
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            };

            _waveOut.Play();

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
            
            _logger.LogInformation("NAudio: Playback completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NAudio: Playback cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NAudio: Error during playback");
            throw;
        }
        finally
        {
            Cleanup();
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("NAudio: Stopping playback");
        _cts?.Cancel();
        _waveOut?.Stop();
        Cleanup();
        return Task.CompletedTask;
    }

    public Task<IAudioPlaybackStream> OpenStreamAsync(
        AudioStreamFormat format,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("NAudio: Opening streaming playback ({Rate}Hz {Channels}ch)",
            format.SampleRate, format.Channels);

        var waveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        var buffer = new BufferedWaveProvider(waveFormat)
        {
            // Enough headroom for a whole response so writes never block on a full buffer.
            BufferDuration          = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = false
        };

        var waveOut = new WaveOutEvent();
        waveOut.Init(buffer);
        waveOut.Play();

        _waveOut = waveOut;
        IsPlaying = true;

        return Task.FromResult<IAudioPlaybackStream>(new NAudioPlaybackStream(this, waveOut, buffer, _logger));
    }

    /// <summary>
    /// Gapless playback backed by a <see cref="BufferedWaveProvider"/>: chunks written
    /// while earlier audio is still playing simply extend the buffer.
    /// </summary>
    private sealed class NAudioPlaybackStream(
        WindowsAudioPlaybackService owner,
        WaveOutEvent waveOut,
        BufferedWaveProvider buffer,
        ILogger logger) : IAudioPlaybackStream
    {
        public Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
        {
            buffer.AddSamples(pcm.ToArray(), 0, pcm.Length);
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Wait for the buffer to drain rather than cutting the tail off.
                while (buffer.BufferedBytes > 0 && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("NAudio: Streaming playback cancelled while draining");
            }
            finally
            {
                waveOut.Stop();
                owner.Cleanup();
            }
        }

        public ValueTask DisposeAsync()
        {
            waveOut.Stop();
            owner.Cleanup();
            return ValueTask.CompletedTask;
        }
    }

    private void Cleanup()
    {
        _waveOut?.Dispose();
        _waveOut = null;
        _audioFileReader?.Dispose();
        _audioFileReader = null;
        IsPlaying = false;
    }

    public void Dispose()
    {
        Cleanup();
        _cts?.Dispose();
    }
}
