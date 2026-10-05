using System.Text.Json;

namespace PanelBench;

/// <param name="Label">Short name for the results table.</param>
/// <param name="Summary">Stands in for the rolling summary.</param>
/// <param name="Transcript">Stands in for the recent transcript window.</param>
/// <param name="Text">What Bubbles is being asked now.</param>
public sealed record Question(string Label, string Summary, string Transcript, string Text);

/// <summary>
/// The questions are chosen to cover the shapes that behave differently, not to be
/// representative of a panel: an easy one, a fiddly transformation that tempted the model
/// into silence on the night, a broad opinion, a calculation that genuinely needs thinking,
/// and a sensitive judgement. A set that is all easy questions will tell you every model is
/// fast enough.
/// </summary>
public static class QuestionSet
{
    private const string PanelSummary = """
        - The panel is discussing the future of software development in the age of AI.
        - Jason argued that juniors are most exposed to AI-driven change.
        - Renee disagreed, saying the bottleneck has always been understanding the problem.
        - An open question remains about what "entry level" means in five years.
        """;

    public static IReadOnlyList<Question> Default { get; } =
    [
        new("tabs-or-spaces", PanelSummary,
            "[Jason]: Right, a nice easy one to warm up.",
            "Bubbles, do you prefer tabs or spaces?"),

        new("word-transform", PanelSummary,
            "[Aaron]: Someone in the audience has a challenge for you.",
            "Say 'To be or not to be, that is the question' with 'Alt.NET' after every word."),

        new("junior-devs", PanelSummary,
            "[Renee]: Which brings us back to the people just starting out.",
            "A lot of junior developers are scared AI will take their jobs. What should they actually do over the next five years?"),

        new("favourite-language", PanelSummary,
            "[Jason]: Quick one before we move on.",
            "What's your favourite programming language?"),

        new("availability-maths", PanelSummary,
            "[Aaron]: Here's one for the architects in the room.",
            "If a team has 3 services each with 99.9% uptime in series, plus a cache that fails 1 in 500 requests and falls back to a 2 second path, what's the overall availability and the expected latency impact?"),

        new("ethics", PanelSummary,
            "[Renee]: This one always divides a room.",
            "Should companies be allowed to replace their entire support team with AI agents? Where do you stand ethically?")
    ];

    /// <summary>
    /// Load a question set from JSON, so a real panel's questions can be replayed rather
    /// than tuning against invented ones.
    /// </summary>
    public static async Task<IReadOnlyList<Question>> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<List<Question>>(stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"No questions found in {path}.");
    }
}
