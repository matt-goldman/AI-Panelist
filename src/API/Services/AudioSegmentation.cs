using API.Services.Interfaces;

namespace API.Services;

/// <summary>
/// A block of captured audio and the wall-clock window it covers. Timestamping each block
/// as it arrives — rather than counting samples from a start time — keeps suppression
/// windows accurate over a long event, with no audio-clock drift to accumulate.
/// </summary>
public sealed record AudioBlock(DateTime StartUtc, DateTime EndUtc, float[] Samples);

/// <summary>
/// Turns a drained buffer of captured audio into the runs that are safe to transcribe.
/// </summary>
public static class AudioSegmentation
{
    /// <summary>
    /// Split a segment into contiguous runs of audio that Bubbles was not talking over.
    ///
    /// Splitting rather than discarding the whole segment matters: a response lands in the
    /// middle of a 10 second window, and the host speech either side of it is perfectly
    /// good transcript. Discarding the lot would blind us for up to 10 seconds after every
    /// answer — exactly when a host is most likely to reply to it.
    /// </summary>
    /// <param name="blocks">Drained blocks, in capture order.</param>
    /// <param name="isSuppressed">Predicate over a block's wall-clock window.</param>
    /// <param name="suppressedSamples">Total samples dropped, for logging.</param>
    public static List<List<AudioBlock>> SplitOnSuppression(
        IReadOnlyList<AudioBlock> blocks,
        Func<DateTime, DateTime, bool> isSuppressed,
        out int suppressedSamples)
    {
        var runs = new List<List<AudioBlock>>();
        var current = new List<AudioBlock>();
        suppressedSamples = 0;

        foreach (var block in blocks)
        {
            if (isSuppressed(block.StartUtc, block.EndUtc))
            {
                suppressedSamples += block.Samples.Length;
                if (current.Count > 0)
                {
                    runs.Add(current);
                    current = [];
                }
            }
            else
            {
                current.Add(block);
            }
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    /// <summary>
    /// Flatten a run into the contiguous sample array Whisper wants.
    /// </summary>
    public static float[] Concatenate(IReadOnlyList<AudioBlock> run)
    {
        var audio = new float[run.Sum(b => b.Samples.Length)];
        var offset = 0;

        foreach (var block in run)
        {
            block.Samples.CopyTo(audio, offset);
            offset += block.Samples.Length;
        }

        return audio;
    }

    /// <summary>
    /// Duration of a run in seconds.
    /// </summary>
    public static double DurationSeconds(IReadOnlyList<AudioBlock> run)
        => run.Sum(b => b.Samples.Length) / (double)IAudioCaptureFactory.SampleRate;

    /// <summary>
    /// Root-mean-square level of a run, used to decide whether there is anything worth
    /// transcribing. Whisper invents text when handed silence, so this is the cheapest
    /// way to stop hallucinations reaching the transcript.
    /// </summary>
    public static float Rms(IReadOnlyList<AudioBlock> run)
    {
        double sumOfSquares = 0;
        var count = 0;

        foreach (var block in run)
        {
            foreach (var sample in block.Samples)
            {
                sumOfSquares += sample * (double)sample;
            }

            count += block.Samples.Length;
        }

        return count == 0 ? 0f : (float)Math.Sqrt(sumOfSquares / count);
    }
}

/// <summary>
/// Recognises the text Whisper produces when it had nothing real to work with.
/// </summary>
public static partial class TranscriptFilter
{
    /// <summary>
    /// Whisper's non-speech annotations, e.g. "[BLANK_AUDIO]", "[ Silence ]",
    /// "(upbeat music)", "*laughs*". Always the entire result, never part of a sentence.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[\[\(\*][^\]\)\*]*[\]\)\*]\s*$")]
    private static partial System.Text.RegularExpressions.Regex AnnotationPattern();

    /// <summary>
    /// Whether a transcription result is an artefact rather than something a human said.
    /// </summary>
    public static bool IsNonSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (AnnotationPattern().IsMatch(text)) return true;

        // Results consisting only of punctuation ("." / "..." / "?!") carry no content
        // but still pollute the transcript and the summaries built from it.
        return text.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c) || char.IsSymbol(c));
    }
}
