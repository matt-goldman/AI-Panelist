using API.Configuration;
using API.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace API.Tests;

/// <summary>
/// Keyword search over the session transcript. Deliberately not semantic: a panel is an
/// hour or two of text, and being able to see exactly why something matched is worth more
/// live than being able to match things that do not share a word.
/// </summary>
public class PanelTranscriptLogTests
{
    private static PanelTranscriptLog Log() =>
        new(NullLogger<PanelTranscriptLog>.Instance, Options.Create(new TranscriptSearchOptions()));

    private static PanelTranscriptLog Panel(out DateTime start)
    {
        var log = Log();
        start = DateTime.UtcNow.AddMinutes(-40);

        log.Add("Welcome everyone to the panel.", start, "Jason");
        log.Add("We moved off Kubernetes last year.", start.AddSeconds(10), "Renee");
        log.Add("It was mostly about cost, the clusters were idle.", start.AddSeconds(15), "Renee");
        log.Add("Interesting, we said the opposite.", start.AddSeconds(20), "Aaron");
        log.Add("Something unrelated about testing.", start.AddMinutes(10), "Jason");
        log.Add("AI agents are said to be the future.", start.AddMinutes(20), "Aaron");

        return log;
    }

    [Fact]
    public void Finds_the_moment_and_returns_it_with_its_context()
    {
        var log = Panel(out _);

        var hits = log.Search("what did Renee say about kubernetes", maxResults: 5, contextEntries: 1);

        var hit = Assert.Single(hits);
        Assert.Contains(hit.Context, line => line.Text.Contains("Kubernetes"));
        Assert.Contains(hit.Context, line => line.Text.Contains("cost"));
    }

    [Fact]
    public void Matches_word_prefixes_but_not_words_that_merely_contain_the_term()
    {
        var log = Panel(out _);

        Assert.Single(log.Search("cluster", 5, 0));            // clusters
        Assert.Single(log.Search("ai", 5, 0));                 // "AI", not "said"
    }

    [Fact]
    public void Adjacent_hits_are_one_moment_rather_than_several()
    {
        var log = Panel(out _);

        // "kubernetes" and "cost" are on consecutive lines: one moment, not two results.
        Assert.Single(log.Search("kubernetes cost", 5, 1));
    }

    [Fact]
    public void A_query_of_only_common_words_finds_nothing()
    {
        var log = Panel(out _);

        Assert.Empty(log.Search("the and of", 5, 1));
        Assert.Empty(PanelTranscriptLog.Terms("what did they say about it"));
    }

    [Fact]
    public void Late_arriving_entries_are_kept_in_spoken_order()
    {
        var log = Panel(out var start);

        // Whisper hands segments over several seconds late and devices interleave, so
        // arrival order is not speech order.
        log.Add("Said right at the start.", start.AddSeconds(5), "Jason");

        var context = log.Search("Kubernetes", 5, 1)[0].Context;
        Assert.Equal("Said right at the start.", context[0].Text);
    }

    [Fact]
    public void Says_so_plainly_when_there_is_no_match()
    {
        var log = Panel(out _);
        var terms = PanelTranscriptLog.Terms("serverless");

        var formatted = PanelTranscriptLog.Format(terms, log.Search("serverless", 5, 1), DateTime.UtcNow);

        Assert.Contains("Nothing in the panel transcript matched", formatted);
    }

    [Fact]
    public void Formats_hits_with_speaker_and_how_long_ago()
    {
        var log = Panel(out _);
        var terms = PanelTranscriptLog.Terms("kubernetes cost");

        var formatted = PanelTranscriptLog.Format(terms, log.Search("kubernetes cost", 5, 1), DateTime.UtcNow);

        Assert.Contains("[Renee]:", formatted);
        Assert.Contains("minute(s) ago", formatted);
    }
}
