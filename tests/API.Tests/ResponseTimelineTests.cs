using API.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace API.Tests;

/// <summary>
/// The timeline is carried ambiently so the hops can be marked from the language model,
/// the chunker and the speech pipeline without threading a timer through all of them.
/// These cover that it actually flows where the pipeline goes, and stops where it should.
/// </summary>
public class ResponseTimelineTests
{
    [Fact]
    public void There_is_no_timeline_until_a_response_starts()
    {
        Assert.Null(ResponseTimeline.Current);

        // Marking with nothing running must be a harmless no-op rather than a crash.
        ResponseTimeline.MarkCurrent("stray");
    }

    [Fact]
    public void Hops_are_recorded_in_order_with_their_detail()
    {
        using (var timeline = ResponseTimeline.Begin(NullLogger.Instance, "button", null))
        {
            timeline.Mark("context.snapshot", "summary 10 characters");
            timeline.Mark("llm.first-answer-token", "no reasoning first");

            var formatted = timeline.Format();

            Assert.Contains("trigger: button", formatted);
            Assert.Contains("context.snapshot", formatted);
            Assert.Contains("summary 10 characters", formatted);
            Assert.True(
                formatted.IndexOf("context.snapshot", StringComparison.Ordinal)
                < formatted.IndexOf("llm.first-answer-token", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_summary_in_flight_at_the_trigger_is_recorded()
    {
        // The single field most likely to explain a slow response.
        using var timeline = ResponseTimeline.Begin(
            NullLogger.Instance, "spoken phrase", TimeSpan.FromMilliseconds(1200));

        Assert.Contains("a summary had been running for 1200ms", timeline.Format());
    }

    [Fact]
    public async Task The_timeline_flows_across_awaits_and_background_tasks()
    {
        // The speech pipeline synthesises on a Task.Run, and the language model is iterated
        // across awaits; both have to find the same timeline.
        using var timeline = ResponseTimeline.Begin(NullLogger.Instance, "button", null);

        await Task.Yield();
        ResponseTimeline.MarkCurrent("after.await");

        await Task.Run(() => ResponseTimeline.MarkCurrent("inside.task-run"));

        var formatted = timeline.Format();
        Assert.Contains("after.await", formatted);
        Assert.Contains("inside.task-run", formatted);
    }

    [Fact]
    public void Disposing_ends_it_so_later_marks_cannot_leak_into_the_next_response()
    {
        var timeline = ResponseTimeline.Begin(NullLogger.Instance, "button", null);
        timeline.Dispose();

        Assert.Null(ResponseTimeline.Current);

        timeline.Mark("too late");
        Assert.DoesNotContain("too late", timeline.Format());
    }
}
