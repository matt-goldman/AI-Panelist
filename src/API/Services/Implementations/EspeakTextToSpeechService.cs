using System.Diagnostics;
using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Text-to-speech using the espeak-ng CLI.
///
/// It sounds like a robot from 1987, which is exactly why it is useful: it needs no GPU,
/// no model download and no network, so the whole capture → STT → LLM → TTS → virtual mic
/// loop can be rehearsed in seconds rather than waiting on Qwen to compile and warm up.
/// Crucially it produces real words, so Whisper can transcribe them — which is what makes
/// the self-suppression gate actually testable.
///
/// It also stands in as a last-resort fallback: if Qwen dies mid-event, a robotic Bubbles
/// beats a silent one.
/// </summary>
public class EspeakTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<EspeakTextToSpeechService> _logger;
    private readonly ResponseCaptureService _captureService;
    private readonly IAudioPlaybackService _playback;
    private readonly string _voice;
    private readonly int _wordsPerMinute;
    private CancellationTokenSource? _cts;

    public bool IsSpeaking { get; private set; }

    public EspeakTextToSpeechService(
        ILogger<EspeakTextToSpeechService> logger,
        IConfiguration configuration,
        ResponseCaptureService captureService,
        IAudioPlaybackService playback)
    {
        _logger = logger;
        _captureService = captureService;
        _playback = playback;

        _voice = configuration["Espeak:Voice"] ?? "en-gb";
        _wordsPerMinute = int.TryParse(configuration["Espeak:WordsPerMinute"], out var wpm) ? wpm : 160;

        _logger.LogInformation("espeak-ng TTS initialized (voice {Voice}, {Wpm} wpm)", _voice, _wordsPerMinute);
        _logger.LogWarning("espeak-ng TTS is for rehearsal and fallback - switch TtsServiceType to Qwen3-TTS for the event");
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            var audioBytes = await SynthesizeAsync(text, _cts.Token);

            if (_cts.Token.IsCancellationRequested) return;

            if (onPlaybackStarting != null)
            {
                await onPlaybackStarting();
            }

            await _playback.PlayAsync(audioBytes, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("espeak-ng TTS: speech cancelled");
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
        // espeak-ng can write WAV to stdout, but the header it emits carries a bogus
        // length. Writing to a file lets it seek back and fix the header up.
        var tempFile = Path.Combine(Path.GetTempPath(), $"bubbles-espeak-{Guid.NewGuid():N}.wav");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName              = "espeak-ng",
                RedirectStandardError = true,
                UseShellExecute       = false
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add(_voice);
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(_wordsPerMinute.ToString());
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(tempFile);
            startInfo.ArgumentList.Add(text);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start espeak-ng. Is it installed?");

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"espeak-ng failed ({process.ExitCode}): {stderr.Trim()}");
            }

            var audioBytes = await File.ReadAllBytesAsync(tempFile, cancellationToken);
            _logger.LogInformation("espeak-ng TTS: synthesized {Chars} characters to {Bytes} bytes",
                text.Length, audioBytes.Length);

            _captureService.CaptureAudioResponse(audioBytes, text);
            return audioBytes;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch (Exception)
            {
                // Temp file cleanup is best effort.
            }
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await _playback.StopAsync();
    }
}
