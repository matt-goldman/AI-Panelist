using System.Diagnostics;
using System.Text.Json;
using API.Configuration;
using API.Services;
using API.Services.Diagnostics;
using API.Services.Implementations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using PanelBench;

// panel-bench: run a fixed question set through the real response path and report what it
// cost, so "is a 4B fast enough?" is a measurement rather than a feeling.
//
// It drives ChatClientLanguageModelService itself rather than the whole API, because what
// is being compared is models, quants, runtimes and prompts - not audio. The one number
// that matters is time to first answer token: everything before it is the model, and
// everything after it is a pipeline that is already fast.

var options = BenchOptions.Parse(args);
if (options is null) return 1;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(console => console.SingleLine = true)
    .SetMinimumLevel(options.Verbose ? LogLevel.Information : LogLevel.Warning));

var questions = options.QuestionsPath is { Length: > 0 } path
    ? await QuestionSet.LoadAsync(path)
    : QuestionSet.Default;

Console.WriteLine($"panel-bench  {options.Describe()}");
Console.WriteLine($"{questions.Count} question(s), {options.Runs} run(s) each, triage {(options.Triage ? "on" : "off")}\n");

IChatClient Client(string model) => options.Provider switch
{
    "ollama" => new OllamaApiClient(new Uri(options.Endpoint), model),
    "openai" => new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(options.ApiKey ?? "none"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) })
        .GetChatClient(model).AsIChatClient(),
    _ => throw new InvalidOperationException($"Unknown provider '{options.Provider}'.")
};

var transcriptLog = new PanelTranscriptLog(
    loggerFactory.CreateLogger<PanelTranscriptLog>(), Options.Create(new TranscriptSearchOptions()));

var service = new ChatClientLanguageModelService(
    loggerFactory.CreateLogger<ChatClientLanguageModelService>(),
    loggerFactory,
    Client(options.Model),
    Client(options.SummaryModel ?? options.Model),
    new PromptService(Options.Create(new PromptConfiguration())),
    transcriptLog,
    Options.Create(new TranscriptSearchOptions()),
    Options.Create(new ResponseTriageOptions { Enabled = options.Triage }));

var results = new List<Measurement>();

foreach (var question in questions)
{
    for (var run = 1; run <= options.Runs; run++)
    {
        var measurement = await MeasureAsync(service, question, options);
        results.Add(measurement);

        Console.WriteLine(measurement.Describe(question, options.Runs > 1 ? run : null));
    }
}

Console.WriteLine();
Console.WriteLine(Measurement.Summarise(results, options));

if (options.JsonPath is { Length: > 0 } jsonPath)
{
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(
        new { options.Model, options.SummaryModel, options.Provider, options.Endpoint, options.Triage, results },
        new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine($"\nWritten to {jsonPath}");
}

return results.Any(r => r.Failed) ? 1 : 0;

static async Task<Measurement> MeasureAsync(
    ChatClientLanguageModelService service,
    Question question,
    BenchOptions options)
{
    var clock = Stopwatch.StartNew();

    double? firstToken = null;
    double? holdingLineAt = null;
    double? answerStartedAt = null;
    string? holdingLine = null;
    var answer = new System.Text.StringBuilder();
    string? failure = null;

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));

        await foreach (var token in service.StreamResponseAsync(
                           question.Summary, question.Transcript, question.Text, timeout.Token))
        {
            // The chunker's flush marker separates a spoken holding line from the answer
            // that follows it, which are two very different latencies.
            if (token == ResponseChunker.Flush)
            {
                holdingLineAt = clock.Elapsed.TotalMilliseconds;
                holdingLine = answer.ToString().Trim();
                answer.Clear();
                continue;
            }

            firstToken ??= clock.Elapsed.TotalMilliseconds;
            if (holdingLine is not null) answerStartedAt ??= clock.Elapsed.TotalMilliseconds;

            answer.Append(token);
        }
    }
    catch (Exception ex)
    {
        failure = $"{ex.GetType().Name}: {ex.Message}";
    }

    var text = answer.ToString().Trim();

    return new Measurement(
        Question: question.Label,
        FirstTokenMs: firstToken,
        HoldingLineMs: holdingLineAt,
        AnswerStartedMs: answerStartedAt,
        TotalMs: clock.Elapsed.TotalMilliseconds,
        Words: text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        Thought: holdingLine is not null,
        HoldingLine: holdingLine,
        Answer: text,
        Failure: failure ?? (text.Length == 0 ? "no answer text" : null));
}
