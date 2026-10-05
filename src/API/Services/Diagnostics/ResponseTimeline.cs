using System.Diagnostics;
using System.Text;

namespace API.Services.Diagnostics;

/// <summary>
/// One response, hop by hop, with elapsed time since the trigger.
///
/// Everything about making this faster is guesswork without it. "Time to first audio:
/// NNNms" says <em>that</em> it was slow, never <em>where</em> — and the candidates are
/// very different problems: waiting on a model that is still thinking, waiting on a model
/// that is queued behind a summary, our own chunking, or the TTS. The hop that matters
/// most is the first answer token, because it splits "the model was slow" from
/// "everything after the model was slow".
///
/// Carried ambiently rather than threaded through every signature: the hops are spread
/// across the orchestrator, the language model, the chunker and the speech pipeline, and
/// passing a timer through all of them would be a worse change than the thing it measures.
/// <see cref="AsyncLocal{T}"/> flows across awaits and Task.Run, which is exactly the
/// shape of this pipeline. Also emitted as an <see cref="Activity"/>, so the same timings
/// appear in the Aspire dashboard without a bespoke log format.
/// </summary>
public sealed class ResponseTimeline : IDisposable
{
    public const string ActivitySourceName = "AIPanelist.Response";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly AsyncLocal<ResponseTimeline?> Ambient = new();

    /// <summary>The timeline for the response running on this execution context, if any.</summary>
    public static ResponseTimeline? Current => Ambient.Value;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<Hop> _hops = [];
    private readonly ILogger _logger;
    private readonly Activity? _activity;
    private readonly string _trigger;
    private readonly Lock _sync = new();

    private bool _completed;

    private ResponseTimeline(ILogger logger, string trigger, TimeSpan? summaryRunningFor)
    {
        _logger = logger;
        _trigger = trigger;
        _activity = Source.StartActivity("response");

        _activity?.SetTag("trigger", trigger);
        _activity?.SetTag("summary.in_flight", summaryRunningFor is not null);

        Mark("trigger", summaryRunningFor is { } running
            ? $"{trigger}; a summary had been running for {running.TotalMilliseconds:F0}ms"
            : trigger);
    }

    /// <summary>
    /// Start timing a response and make it ambient. Dispose at the end, which logs it.
    /// </summary>
    /// <param name="summaryRunningFor">
    /// How long a summary had been in flight when the trigger arrived, or null if none was.
    /// This single field is the one most likely to explain a slow response, since one model
    /// on one GPU means a summary mid-flight owns it.
    /// </param>
    public static ResponseTimeline Begin(ILogger logger, string trigger, TimeSpan? summaryRunningFor)
    {
        var timeline = new ResponseTimeline(logger, trigger, summaryRunningFor);
        Ambient.Value = timeline;
        return timeline;
    }

    /// <summary>Record a hop. Safe to call from any thread, and a no-op if nothing is timing.</summary>
    public void Mark(string hop, string? detail = null)
    {
        lock (_sync)
        {
            if (_completed) return;
            _hops.Add(new Hop(hop, _stopwatch.Elapsed, detail));
        }

        _activity?.AddEvent(new ActivityEvent(hop,
            tags: detail is null ? default : new ActivityTagsCollection { ["detail"] = detail }));
    }

    /// <summary>Record a hop on the ambient timeline, if there is one.</summary>
    public static void MarkCurrent(string hop, string? detail = null) => Current?.Mark(hop, detail);

    /// <summary>Elapsed since the trigger, for callers that want to log their own number.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_completed) return;
            _completed = true;
        }

        _activity?.SetTag("duration_ms", (long)_stopwatch.Elapsed.TotalMilliseconds);
        _activity?.Dispose();

        if (Ambient.Value == this) Ambient.Value = null;

        _logger.LogInformation("Response timeline:\n{Timeline}", Format());
    }

    /// <summary>
    /// The timeline as a table. One block per response so it can be read at a glance in a
    /// console that is also carrying transcription debug.
    /// </summary>
    public string Format()
    {
        List<Hop> hops;
        lock (_sync) hops = [.. _hops];

        var builder = new StringBuilder();
        builder.AppendLine($"  trigger: {_trigger}   total: {_stopwatch.Elapsed.TotalMilliseconds:F0}ms");

        var previous = TimeSpan.Zero;
        foreach (var hop in hops)
        {
            var gap = hop.Elapsed - previous;
            previous = hop.Elapsed;

            builder.Append($"  {hop.Elapsed.TotalMilliseconds,7:F0}ms {("+" + gap.TotalMilliseconds.ToString("F0") + "ms"),9}  {hop.Name}");
            if (hop.Detail is { Length: > 0 }) builder.Append($"  — {hop.Detail}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private readonly record struct Hop(string Name, TimeSpan Elapsed, string? Detail);
}
