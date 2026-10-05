using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using API.Configuration;
using API.Services.Diagnostics;
using API.Services.Implementations.PipeWire;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// What a streamed response cost and produced.
/// </summary>
/// <param name="Text">The full response, reassembled from its chunks.</param>
/// <param name="TimeToFirstAudio">Trigger to first sound. The metric that matters.</param>
/// <param name="ChunkCount">How many pieces it was spoken in.</param>
public sealed record StreamedSpeechResult(string Text, TimeSpan TimeToFirstAudio, int ChunkCount);

/// <summary>
/// Turns a stream of LLM tokens into continuous speech.
///
/// Chunk N+1 is synthesised while chunk N is still playing, so after the first chunk the
/// audio never catches up with generation — generation-to-speech has plenty of headroom
/// under realtime. Everything is written into one playback stream rather than played as
/// separate clips, so there is no gap at the seams.
/// </summary>
public class StreamingSpeechPipeline(
    ILogger<StreamingSpeechPipeline> logger,
    ITextToSpeechService tts,
    IAudioPlaybackService playback,
    ResponseChunker chunker,
    SpeechEnvelopePublisher envelopes,
    IOptions<StreamingResponseOptions> options)
{
    private readonly StreamingResponseOptions _options = options.Value;

    /// <summary>
    /// Speak a token stream.
    /// </summary>
    /// <param name="tokens">Answer text as the model produces it.</param>
    /// <param name="onFirstAudio">
    /// Invoked immediately before the first sound is heard — where the caller flips to the
    /// Speaking state and opens the self-suppression window.
    /// </param>
    public async Task<StreamedSpeechResult> SpeakAsync(
        IAsyncEnumerable<string> tokens,
        Func<Task>? onFirstAudio,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var spoken = new StringBuilder();
        envelopes.Begin();
        var timeToFirstAudio = TimeSpan.Zero;
        var chunkCount = 0;

        // Bounded so synthesis stays a little ahead of playback without running away:
        // a deep queue would only delay how quickly a cancel takes effect.
        var synthesised = Channel.CreateBounded<SynthesisedChunk>(
            new BoundedChannelOptions(Math.Max(1, _options.SynthesisLookahead))
            {
                SingleReader = true,
                SingleWriter = true
            });

        var producer = Task.Run(
            () => SynthesiseAsync(tokens, synthesised.Writer, cancellationToken), CancellationToken.None);

        IAudioPlaybackStream? stream = null;
        AudioStreamFormat? streamFormat = null;

        try
        {
            await foreach (var chunk in synthesised.Reader.ReadAllAsync(cancellationToken))
            {
                var wav = WavAudio.Parse(chunk.Audio);
                var pcm = WavAudio.ToPcm16(wav);

                if (stream is null)
                {
                    stream = await playback.OpenStreamAsync(wav.Format, cancellationToken);
                    streamFormat = wav.Format;

                    if (onFirstAudio is not null)
                    {
                        await onFirstAudio();
                    }

                    timeToFirstAudio = stopwatch.Elapsed;
                    ResponseTimeline.MarkCurrent("audio.first-sample", $"\"{chunk.Text}\"");
                    logger.LogInformation(
                        "Time to first audio: {Ms}ms (first chunk: \"{Chunk}\")",
                        (int)timeToFirstAudio.TotalMilliseconds, chunk.Text);
                }
                else if (streamFormat != wav.Format)
                {
                    // Shouldn't happen with a single TTS backend, but a silent format
                    // mismatch would come out as noise, which is worse than a small gap.
                    logger.LogWarning(
                        "TTS changed format mid-response ({Old} -> {New}); restarting playback stream",
                        streamFormat, wav.Format);

                    await stream.CompleteAsync(cancellationToken);
                    await stream.DisposeAsync();

                    stream = await playback.OpenStreamAsync(wav.Format, cancellationToken);
                    streamFormat = wav.Format;

                    // A new stream restarts the clock the envelope schedule is measured from.
                    envelopes.Begin();
                }

                await stream.WriteAsync(pcm, cancellationToken);

                // Measured from the PCM actually queued, so the display's mouth is driven
                // by the same bytes the audience hears.
                envelopes.Publish(pcm, wav.Format);

                spoken.Append(spoken.Length > 0 ? " " : string.Empty).Append(chunk.Text);
                chunkCount++;
            }

            await producer;

            // Before the drain, not after: the envelope already says when the last frame
            // is heard, and the drain is just the output buffer emptying. Published late,
            // the display would sit in its between-chunks bridge after the audio had ended.
            envelopes.Complete();

            if (stream is not null)
            {
                // Drain rather than cut: the last chunk is still playing at this point.
                await stream.CompleteAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Streamed response cancelled after {Count} chunk(s)", chunkCount);

            // A cancelled response has stopped making noise, so the mouth must stop too
            // rather than holding its last shape.
            envelopes.Complete();
            throw;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }

        logger.LogInformation(
            "Streamed response complete: {Chunks} chunks, first audio at {Ms}ms, {Total}ms total",
            chunkCount, (int)timeToFirstAudio.TotalMilliseconds, (int)stopwatch.ElapsedMilliseconds);

        return new StreamedSpeechResult(spoken.ToString(), timeToFirstAudio, chunkCount);
    }

    private async Task SynthesiseAsync(
        IAsyncEnumerable<string> tokens,
        ChannelWriter<SynthesisedChunk> writer,
        CancellationToken cancellationToken)
    {
        var first = true;

        try
        {
            await foreach (var chunk in chunker.ChunkAsync(tokens, cancellationToken))
            {
                var text = SpeechText.Clean(chunk);
                if (text.Length == 0) continue;

                var audio = await tts.SynthesizeAsync(text, cancellationToken);

                ResponseTimeline.MarkCurrent(
                    first ? "tts.first-chunk-synthesised" : "tts.chunk-synthesised",
                    $"{audio.Length / 1024}KB");
                first = false;

                await writer.WriteAsync(new SynthesisedChunk(text, audio), cancellationToken);
            }

            writer.Complete();
        }
        catch (Exception ex)
        {
            // Surfaces on the reader side, so a synthesis failure doesn't hang playback.
            writer.Complete(ex);
        }
    }

    private sealed record SynthesisedChunk(string Text, byte[] Audio);
}
