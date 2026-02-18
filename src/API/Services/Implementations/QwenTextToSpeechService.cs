using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

/// <summary>
/// Text-to-speech service using Qwen TTS via HTTP endpoint
/// </summary>
public class QwenTextToSpeechService : ITextToSpeechService
{
    private readonly HttpClient _client;
    private readonly ILogger<QwenTextToSpeechService> _logger;
    private readonly ResponseCaptureService _captureService;
    private CancellationTokenSource? _cts;
    private WaveOutEvent? _waveOut;
    private readonly object _lock = new();

    public bool IsSpeaking { get; private set; }

    public QwenTextToSpeechService(
        HttpClient client,
        ILogger<QwenTextToSpeechService> logger,
        ResponseCaptureService captureService)
    {
        _client = client;
        _logger = logger;
        _captureService = captureService;
        _logger.LogInformation("Qwen TTS initialized");
        _logger.LogInformation("Base address for HTTP client: {BaseAddress}", _client.BaseAddress);
        if (_client.BaseAddress is null)
        {
            throw new Exception("Qwen TTS HTTP client base address is null. Please check configuration.");
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null)
    {
        _logger.LogInformation("Qwen TTS: Speaking {Length} characters", text.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            // Get audio bytes from Qwen TTS endpoint
            _logger.LogInformation("Qwen TTS: Calling synthesis API...");
            var audioBytes = await SynthesizeAsync(text, _cts.Token);
            _logger.LogInformation("Qwen TTS: Synthesis complete, received {Bytes} bytes", audioBytes.Length);

            // Capture audio bytes asynchronously (non-blocking, zero latency impact)
            _captureService.CaptureAudioResponse(audioBytes, text);

            if (_cts.Token.IsCancellationRequested)
            {
                _logger.LogInformation("Qwen TTS: Cancelled before playback");
                return;
            }

            // Notify caller that playback is about to start
            if (onPlaybackStarting != null)
            {
                await onPlaybackStarting();
            }

            // Play audio to host speaker using NAudio
            await PlayAudioAsync(audioBytes, _cts.Token);

            _logger.LogInformation("Qwen TTS: Speech completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Qwen TTS: Speech cancelled by user");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Qwen TTS: Error calling TTS endpoint");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qwen TTS: Error during speech synthesis");
            throw;
        }
        finally
        {
            IsSpeaking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Qwen TTS: Stopping speech");
        _cts?.Cancel();

        lock (_lock)
        {
            if (_waveOut is not null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var response = await _client.PostAsJsonAsync("/tts", new { text, language = "English" }, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Response is raw audio WAV bytes
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task PlayAudioAsync(byte[] audioBytes, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();

        // Create streams that will be kept alive until playback completes
        var memoryStream = new MemoryStream(audioBytes);
        WaveFileReader? waveStream = null;

        try
        {
            waveStream = new WaveFileReader(memoryStream);

            lock (_lock)
            {
                _waveOut = new WaveOutEvent();
                _waveOut.Init(waveStream);
            }

            _waveOut.PlaybackStopped += (sender, args) =>
            {
                if (args.Exception is not null)
                {
                    tcs.TrySetException(args.Exception);
                }
                else
                {
                    tcs.TrySetResult();
                }
            };

            using var registration = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    _waveOut?.Stop();
                }
                tcs.TrySetCanceled(cancellationToken);
            });

            _logger.LogInformation("Qwen TTS: Starting audio playback");
            _waveOut.Play();

            await tcs.Task;
        }
        finally
        {
            // Dispose streams after playback completes
            waveStream?.Dispose();
            memoryStream.Dispose();

            lock (_lock)
            {
                _waveOut?.Dispose();
                _waveOut = null;
            }
        }
    }
}
