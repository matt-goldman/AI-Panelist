namespace PanelBench;

/// <summary>
/// What to measure and where. Everything is a flag rather than configuration, because the
/// whole point is running the same question set against several setups in a row.
/// </summary>
public sealed class BenchOptions
{
    public string Provider { get; private init; } = "ollama";
    public string Endpoint { get; private init; } = "http://localhost:11434";
    public string Model { get; private init; } = "qwen3.8:latest";
    public string? SummaryModel { get; private init; }
    public string? ApiKey { get; private init; }
    public bool Triage { get; private init; } = true;
    public int Runs { get; private init; } = 1;
    public int TimeoutSeconds { get; private init; } = 120;
    public bool Verbose { get; private init; }
    public string? QuestionsPath { get; private init; }
    public string? JsonPath { get; private init; }

    public string Describe() =>
        $"{Provider} {Model}" + (SummaryModel is null ? "" : $" (summaries: {SummaryModel})") + $" at {Endpoint}";

    public static BenchOptions? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return null;
        }

        string? Value(string name) =>
            Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        try
        {
            return new BenchOptions
            {
                Provider       = Value("--provider")?.ToLowerInvariant() ?? "ollama",
                Endpoint       = Value("--endpoint") ?? "http://localhost:11434",
                Model          = Value("--model") ?? "qwen3.8:latest",
                SummaryModel   = Value("--summary-model"),
                // Never a flag: a key on the command line ends up in shell history.
                ApiKey         = Environment.GetEnvironmentVariable("PANEL_BENCH_API_KEY"),
                Triage         = !args.Contains("--no-triage"),
                Runs           = int.Parse(Value("--runs") ?? "1"),
                TimeoutSeconds = int.Parse(Value("--timeout") ?? "120"),
                Verbose        = args.Contains("--verbose"),
                QuestionsPath  = Value("--questions"),
                JsonPath       = Value("--json")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the arguments: {ex.Message}\n\n{Usage}");
            return null;
        }
    }

    private const string Usage = """
        panel-bench - measure what a model costs on the response path.

        Usage:
          dotnet run --project tools/PanelBench -- [options]

        Options:
          --provider <ollama|openai>   Defaults to ollama. Use openai for llama.cpp, vLLM and gateways.
          --endpoint <url>             Defaults to http://localhost:11434
          --model <name>               The model answering. Defaults to qwen3.8:latest
          --summary-model <name>       A different model for summaries. Defaults to the same one.
          --no-triage                  Measure the old behaviour: one pass, model's own reasoning.
          --runs <n>                   Repeat each question n times. Use 2+ once the model is warm.
          --timeout <seconds>          Give up on a question. Defaults to 120.
          --questions <file.json>      A question set. Omit for the built-in one.
          --json <file.json>           Write the measurements out for comparison.
          --verbose                    Show the service's own logging.

        The API key, when one is needed, comes from PANEL_BENCH_API_KEY rather than a flag.

        Examples:
          dotnet run --project tools/PanelBench -- --model qwen3:4b --runs 3
          dotnet run --project tools/PanelBench -- --provider openai --endpoint http://box:8080/v1 --model local
          dotnet run --project tools/PanelBench -- --no-triage --json before.json
        """;
}
