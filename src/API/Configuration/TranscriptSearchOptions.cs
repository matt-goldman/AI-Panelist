namespace API.Configuration;

/// <summary>
/// Lets Bubbles look up something said earlier in the panel, rather than relying on
/// whatever survived summarisation and the few-minute rolling transcript.
///
/// Deliberately unfancy: keyword matching over plain text, no embeddings. A panel is one
/// to two hours of transcript; substring search over that is instant and you can see
/// exactly why something matched.
/// </summary>
public class TranscriptSearchOptions
{
    public const string SectionName = "TranscriptSearch";

    /// <summary>
    /// Offer the search tool to the model. Only the ChatClient LLM path supports tools.
    /// Off, responses are generated exactly as before. The session log below is kept
    /// either way, so the search endpoint works for checking what it would find.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Where to write the whole session's transcript as JSON lines, one file per run.
    /// Null keeps it in memory only.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Most moments returned for one search.
    /// </summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// Transcript entries either side of a hit included with it. Whisper segments are
    /// sentence-ish, so one either side usually gives the point and its reply.
    /// </summary>
    public int ContextEntries { get; set; } = 1;

    /// <summary>
    /// Searches allowed per response. Every search is another round trip through the
    /// model before any audio, so keep this small.
    /// </summary>
    public int MaxSearchesPerResponse { get; set; } = 1;

    /// <summary>
    /// Sent to the model as a system message when the tool is offered.
    /// </summary>
    public string Guidance { get; set; } =
        "Your context only covers a summary and the last minute or so of the panel. "
        + "If someone refers to something said earlier that isn't in that context, use the "
        + "search_panel_transcript tool to look it up before answering. Don't search otherwise.";
}
