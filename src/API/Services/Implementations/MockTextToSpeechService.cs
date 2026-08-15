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

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null)
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

            // Notify caller that playback is about to start
            if (onPlaybackStarting != null)
            {
                await onPlaybackStarting();
            }

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

    public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        // Silence of a realistic length, as a valid WAV, so the streaming pipeline can be
        // exercised end-to-end (chunking, queueing, timings) without a GPU or network.
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var durationMs = Math.Max(200, wordCount * 100);

        _logger.LogInformation("Mock TTS: Synthesizing {Words} words as {Duration}ms of silence", wordCount, durationMs);

        return Task.FromResult(CreateSilenceWav(SampleRate, durationMs));
    }

    private const int SampleRate = 24000;

    private static byte[] CreateSilenceWav(int sampleRate, int durationMs)
    {
        var sampleCount = sampleRate * durationMs / 1000;
        var dataBytes = sampleCount * 2; // 16-bit mono

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                       // fmt chunk size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);

        writer.Flush();
        return stream.ToArray();
    }
}
