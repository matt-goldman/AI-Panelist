using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Text-to-speech service using Qwen TTS via HTTP endpoint.
///
/// Synthesis and playback are deliberately separate: <see cref="SynthesizeAsync"/> returns
/// WAV bytes so the streamed response path can generate the next chunk while the current one
/// plays, and playback goes through <see cref="IAudioPlaybackService"/> so the audio lands in
/// whichever sink the platform backend is pointed at (the AI guest's virtual mic on Linux).
/// </summary>
public class QwenTextToSpeechService : ITextToSpeechService
{
    private readonly HttpClient _client;
    private readonly ILogger<QwenTextToSpeechService> _logger;
    private readonly ResponseCaptureService _captureService;
    private readonly IAudioPlaybackService _playback;
    private CancellationTokenSource? _cts;

    public bool IsSpeaking { get; private set; }

    public QwenTextToSpeechService(
        HttpClient client,
        ILogger<QwenTextToSpeechService> logger,
        ResponseCaptureService captureService,
        IAudioPlaybackService playback)
    {
        _client = client;
        _logger = logger;
        _captureService = captureService;
        _playback = playback;

        _logger.LogInformation("Qwen TTS initialized");
        _logger.LogInformation("Base address for HTTP client: {BaseAddress}", _client.BaseAddress);

        if (_client.BaseAddress is null)
        {
            throw new InvalidOperationException("Qwen TTS HTTP client base address is null. Please check configuration.");
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null)
    {
        _logger.LogInformation("Qwen TTS: Speaking {Length} characters", text.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            var audioBytes = await SynthesizeAsync(text, _cts.Token);

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

            await _playback.PlayAsync(audioBytes, _cts.Token);

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

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Qwen TTS: Synthesizing {Length} characters", text.Length);

        var response = await _client.PostAsJsonAsync("/tts", new { text, language = "English" }, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Response is raw audio WAV bytes
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        _logger.LogDebug("Qwen TTS: Synthesis complete, received {Bytes} bytes", audioBytes.Length);

        // Capture audio bytes asynchronously (non-blocking, zero latency impact)
        _captureService.CaptureAudioResponse(audioBytes, text);

        return audioBytes;
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Qwen TTS: Stopping speech");
        _cts?.Cancel();
        await _playback.StopAsync();
    }
}
