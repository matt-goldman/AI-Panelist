using System.Text;

namespace PanelBench;

/// <param name="FirstTokenMs">
/// Time to the first word Bubbles would say. The number that matters: everything before it
/// is the model, everything after it is a pipeline that is already fast.
/// </param>
/// <param name="HoldingLineMs">When the holding line was complete, if the model asked to think.</param>
/// <param name="AnswerStartedMs">When the real answer began, after thinking.</param>
/// <param name="Thought">Whether triage decided this one needed thinking about.</param>
public sealed record Measurement(
    string Question,
    double? FirstTokenMs,
    double? HoldingLineMs,
    double? AnswerStartedMs,
    double TotalMs,
    int Words,
    bool Thought,
    string? HoldingLine,
    string Answer,
    string? Failure)
{
    public bool Failed => Failure is not null;

    public string Describe(Question question, int? run)
    {
        var builder = new StringBuilder();
        builder.Append($"  {Question,-20}");
        builder.Append(run is null ? "     " : $" #{run}  ");

        if (Failed)
        {
            builder.Append($"FAILED after {TotalMs / 1000:F1}s — {Failure}");
            return builder.ToString();
        }

        builder.Append($"first word {FirstTokenMs,6:F0}ms");

        if (Thought)
        {
            builder.Append($"  THINK→ answer at {AnswerStartedMs,6:F0}ms");
        }

        builder.Append($"   total {TotalMs / 1000,5:F1}s   {Words,3} words");

        if (Thought && HoldingLine is { Length: > 0 })
        {
            builder.Append($"\n{new string(' ', 26)}holding: \"{HoldingLine}\"");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The comparison table. Medians rather than means, because one cold start should not
    /// decide whether a model is fast.
    /// </summary>
    public static string Summarise(IReadOnlyList<Measurement> results, BenchOptions options)
    {
        var ok = results.Where(r => !r.Failed).ToList();
        var builder = new StringBuilder();

        builder.AppendLine($"{options.Describe()}   triage {(options.Triage ? "on" : "off")}");
        builder.AppendLine(new string('-', 72));

        if (ok.Count == 0)
        {
            builder.AppendLine("Every question failed. Check the endpoint and the model name.");
            return builder.ToString();
        }

        var firstWords = ok.Where(r => r.FirstTokenMs is not null).Select(r => r.FirstTokenMs!.Value).ToList();
        var thought = ok.Where(r => r.Thought).ToList();
        var straight = ok.Where(r => !r.Thought).ToList();

        builder.AppendLine($"  answered straight away   {straight.Count}/{ok.Count}");
        builder.AppendLine($"  first word   median {Median(firstWords):F0}ms   worst {firstWords.Max():F0}ms");

        if (straight.Count > 0)
        {
            builder.AppendLine($"    of those   median {Median([.. straight.Select(r => r.FirstTokenMs!.Value)]):F0}ms");
        }

        if (thought.Count > 0)
        {
            var answers = thought.Where(r => r.AnswerStartedMs is not null).Select(r => r.AnswerStartedMs!.Value).ToList();
            builder.AppendLine($"  needed thinking          {thought.Count}/{ok.Count}   "
                               + $"answer began median {(answers.Count > 0 ? Median(answers) : 0):F0}ms");
        }

        builder.AppendLine($"  whole response   median {Median([.. ok.Select(r => r.TotalMs)]) / 1000:F1}s");
        builder.AppendLine($"  answer length    median {Median([.. ok.Select(r => (double)r.Words)]):F0} words");

        var failures = results.Where(r => r.Failed).ToList();
        if (failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"  {failures.Count} FAILED:");
            foreach (var failure in failures)
            {
                builder.AppendLine($"    {failure.Question}: {failure.Failure}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
