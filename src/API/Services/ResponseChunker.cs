using System.Runtime.CompilerServices;
using System.Text;
using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Cuts a stream of LLM tokens into chunks that can each be spoken on their own.
///
/// Boundaries are always punctuation, never token counts. Sentence terminators are
/// preferred; a strong clause break is accepted once a chunk is long enough; and only as a
/// last resort — a run-on with no punctuation at all — do we cut at a word boundary.
///
/// The first chunk is deliberately shorter than the rest: it sets time-to-first-audio.
/// Later chunks are longer because playback of the previous one is buying time, and a
/// longer span gives the TTS more context for natural intonation.
/// </summary>
public class ResponseChunker(IOptions<StreamingResponseOptions> options)
{
    private readonly StreamingResponseOptions _options = options.Value;

    private static readonly char[] SentenceEnders = ['.', '!', '?'];
    private static readonly char[] ClauseBreaks = [',', ';', ':', '—', '–'];

    /// <summary>
    /// Abbreviations whose trailing dot is not the end of a sentence. Without this,
    /// "e.g." and "Dr." split mid-phrase and the TTS pauses in the wrong place.
    /// </summary>
    private static readonly string[] Abbreviations =
    [
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st",
        "e.g", "i.e", "etc", "vs", "approx", "inc", "ltd", "co",
        "a.m", "p.m", "u.s", "u.k"
    ];

    /// <summary>
    /// Chunk a token stream. Chunks are yielded as soon as a boundary is found, so the
    /// caller can start synthesising while the model is still writing.
    /// </summary>
    public async IAsyncEnumerable<string> ChunkAsync(
        IAsyncEnumerable<string> tokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        var isFirstChunk = true;

        await foreach (var token in tokens.WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(token)) continue;
            buffer.Append(token);

            // One token can complete more than one boundary.
            while (true)
            {
                var split = FindSplit(buffer.ToString(), isFirstChunk);
                if (split <= 0) break;

                var chunk = buffer.ToString(0, split).Trim();
                buffer.Remove(0, split);

                if (chunk.Length == 0) continue;

                isFirstChunk = false;
                yield return chunk;
            }
        }

        var remainder = buffer.ToString().Trim();
        if (remainder.Length > 0)
        {
            yield return remainder;
        }
    }

    /// <summary>
    /// Index to cut at, or -1 if the buffer should keep growing.
    /// </summary>
    private int FindSplit(string buffer, bool isFirstChunk)
    {
        var minChars = isFirstChunk ? _options.FirstChunkMinChars : _options.MinChunkChars;
        var maxChars = isFirstChunk ? _options.FirstChunkMaxChars : _options.MaxChunkChars;

        if (buffer.Trim().Length < minChars) return -1;

        // Preferred: a real sentence ending at or after the minimum length.
        var searchLimit = Math.Min(buffer.Length, maxChars);
        for (var i = minChars - 1; i < searchLimit; i++)
        {
            if (!SentenceEnders.Contains(buffer[i])) continue;
            if (!IsSentenceEnd(buffer, i)) continue;

            // Include any trailing quote or bracket that belongs to the sentence.
            var end = i + 1;
            while (end < buffer.Length && (buffer[end] == '"' || buffer[end] == '\'' || buffer[end] == ')'))
            {
                end++;
            }

            return end;
        }

        // Acceptable: a strong clause break, once the chunk carries enough to stand alone.
        for (var i = minChars - 1; i < searchLimit; i++)
        {
            if (ClauseBreaks.Contains(buffer[i]) && IsFollowedByBreak(buffer, i))
            {
                return i + 1;
            }
        }

        // Last resort: no punctuation in range, so cut at a word boundary rather than
        // letting the chunk grow without limit.
        if (buffer.Length >= maxChars)
        {
            var lastSpace = buffer.LastIndexOf(' ', Math.Min(maxChars, buffer.Length - 1));
            return lastSpace > minChars ? lastSpace + 1 : maxChars;
        }

        return -1;
    }

    /// <summary>
    /// Whether the terminator at <paramref name="index"/> really ends a sentence, rather
    /// than being a decimal point or part of an abbreviation.
    /// </summary>
    private static bool IsSentenceEnd(string buffer, int index)
    {
        if (!IsFollowedByBreak(buffer, index)) return false;

        if (buffer[index] == '.')
        {
            // "3.5" - a decimal, not a full stop.
            if (index > 0 && char.IsDigit(buffer[index - 1])
                          && index + 1 < buffer.Length && char.IsDigit(buffer[index + 1]))
            {
                return false;
            }

            var wordStart = index;
            while (wordStart > 0 && !char.IsWhiteSpace(buffer[wordStart - 1])) wordStart--;

            var word = buffer[wordStart..index].ToLowerInvariant();
            if (Abbreviations.Contains(word)) return false;
        }

        return true;
    }

    /// <summary>
    /// A boundary only counts if whitespace follows it. Mid-stream that means waiting for
    /// one more token, which costs nothing and avoids splitting inside "3.5" or "e.g.".
    /// </summary>
    private static bool IsFollowedByBreak(string buffer, int index)
        => index + 1 < buffer.Length && char.IsWhiteSpace(buffer[index + 1]);
}

/// <summary>
/// Strips formatting an LLM may emit that a TTS would read out literally.
/// </summary>
public static class SpeechText
{
    /// <summary>
    /// Remove markdown emphasis, code markers and heading hashes. The system prompt already
    /// asks for none of it, but "*wink*" read aloud as "asterisk wink asterisk" is a bad
    /// enough failure on a live stream to be worth belt and braces.
    /// </summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        var atLineStart = true;

        foreach (var c in text)
        {
            switch (c)
            {
                case '*' or '_' or '`' or '~':
                    continue;
                case '#' when atLineStart:
                    continue;
                case '\n':
                    atLineStart = true;
                    builder.Append(' ');
                    continue;
            }

            if (!char.IsWhiteSpace(c)) atLineStart = false;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }
}
