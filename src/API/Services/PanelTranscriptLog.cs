using System.Text;
using System.Text.Json;
using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Everything said in this session — hosts and Bubbles alike — kept for the whole run,
/// unlike <see cref="TranscriptBufferService"/>, which forgets after a few minutes.
///
/// Searched by keyword, not meaning. A term matches the start of a word, so "container"
/// finds "containers" but "ai" doesn't find "said". Every hit reports the terms it matched,
/// so a surprising result can be explained on the night.
/// </summary>
public sealed class PanelTranscriptLog : IDisposable
{
    public const string BubblesSpeaker = "Bubbles";

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "did", "do", "does", "for", "from",
        "had", "has", "have", "he", "her", "his", "how", "i", "if", "in", "is", "it", "its", "me",
        "my", "of", "on", "or", "our", "she", "so", "that", "the", "their", "them", "then", "there",
        "they", "this", "to", "us", "was", "we", "were", "what", "when", "where", "which", "who",
        "why", "with", "you", "your", "about", "said", "say", "says", "saying", "talk", "talked",
        "talking", "mention", "mentioned", "earlier", "before", "someone", "something", "bubbles",
        "just", "like", "really", "think", "thought", "point", "made"
    ];

    private readonly ILogger<PanelTranscriptLog> _logger;
    private readonly List<Entry> _entries = [];
    private readonly Lock _sync = new();
    private StreamWriter? _writer;

    public PanelTranscriptLog(ILogger<PanelTranscriptLog> logger, IOptions<TranscriptSearchOptions> options)
    {
        _logger = logger;

        if (options.Value.LogDirectory is { Length: > 0 } directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"panel-transcript_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                _logger.LogInformation("Writing the session transcript to {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't open a session transcript file in {Directory}; keeping it in memory only", directory);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    public void Add(string text, DateTime timestampUtc, string? speaker)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var entry = new Entry(timestampUtc, speaker, text.Trim(), $" {TriggerPhraseMatcher.Normalise(text)} ");

        lock (_sync)
        {
            // Whisper hands segments over a few seconds late and devices interleave, so keep
            // the log in spoken order rather than arrival order.
            var index = _entries.Count;
            while (index > 0 && _entries[index - 1].TimestampUtc > timestampUtc) index--;
            _entries.Insert(index, entry);

            if (_writer is not null)
            {
                try
                {
                    _writer.WriteLine(JsonSerializer.Serialize(new { t = timestampUtc, speaker, text = entry.Text }));
                }
                catch (Exception ex)
                {
                    // Never let the log take the live path down.
                    _logger.LogWarning(ex, "Writing the session transcript failed; keeping it in memory only from here");
                    _writer.Dispose();
                    _writer = null;
                }
            }
        }
    }

    /// <summary>
    /// The query's distinctive words, as they'll be matched.
    /// </summary>
    public static IReadOnlyList<string> Terms(string query) =>
        TriggerPhraseMatcher.Normalise(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StopWords.Contains(t))
            .Distinct()
            .ToList();

    /// <summary>
    /// Moments in the session matching the most query terms, best first, with neighbouring
    /// entries for context. Ties go to the more recent moment.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int maxResults, int contextEntries)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return [];

        var phrase = terms.Count > 1 ? $" {TriggerPhraseMatcher.Normalise(query)} " : null;

        lock (_sync)
        {
            var scored = new List<(int Index, int Score, List<string> Matched)>();

            for (var i = 0; i < _entries.Count; i++)
            {
                var text = _entries[i].Normalised;
                var matched = terms.Where(t => text.Contains($" {t}", StringComparison.Ordinal)).ToList();
                if (matched.Count == 0) continue;

                // The whole query appearing verbatim beats the same words scattered about.
                var score = matched.Count + (phrase is not null && text.Contains(phrase, StringComparison.Ordinal) ? terms.Count : 0);
                scored.Add((i, score, matched));
            }

            var hits = new List<SearchHit>();
            var taken = new List<int>();

            foreach (var (index, score, matched) in scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Index))
            {
                if (hits.Count >= maxResults) break;

                // Neighbouring hits are the same moment; one is enough.
                if (taken.Any(t => Math.Abs(t - index) <= contextEntries)) continue;
                taken.Add(index);

                var first = Math.Max(0, index - contextEntries);
                var last = Math.Min(_entries.Count - 1, index + contextEntries);
                var context = _entries.GetRange(first, last - first + 1)
                    .Select(e => new TranscriptLine(e.TimestampUtc, e.Speaker, e.Text))
                    .ToList();

                hits.Add(new SearchHit(_entries[index].TimestampUtc, score, matched, context));
            }

            return hits;
        }
    }

    /// <summary>
    /// A search result as the model will read it.
    /// </summary>
    public static string Format(IReadOnlyList<string> terms, IReadOnlyList<SearchHit> hits, DateTime nowUtc)
    {
        if (terms.Count == 0)
        {
            return "That search had no distinctive words in it. Search for the specific topic, name or term.";
        }

        if (hits.Count == 0)
        {
            return $"Nothing in the panel transcript matched: {string.Join(", ", terms)}. "
                   + "Answer from what you know, and say so if you can't recall it.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{hits.Count} moment(s) from earlier in the panel matched {string.Join(", ", terms)}. "
                           + "This is a speech-to-text transcript, so expect transcription errors.");

        foreach (var hit in hits)
        {
            var minutesAgo = (int)Math.Round((nowUtc - hit.TimestampUtc).TotalMinutes);
            builder.AppendLine();
            builder.AppendLine(minutesAgo < 1 ? "--- Under a minute ago:" : $"--- About {minutesAgo} minute(s) ago:");

            foreach (var line in hit.Context)
            {
                builder.AppendLine(line.Speaker is { Length: > 0 } speaker ? $"[{speaker}]: {line.Text}" : line.Text);
            }
        }

        return builder.ToString().TrimEnd();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed record Entry(DateTime TimestampUtc, string? Speaker, string Text, string Normalised);
}

public sealed record TranscriptLine(DateTime TimestampUtc, string? Speaker, string Text);

public sealed record SearchHit(DateTime TimestampUtc, int Score, IReadOnlyList<string> MatchedTerms, IReadOnlyList<TranscriptLine> Context);
