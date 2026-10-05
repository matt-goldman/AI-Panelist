using API.Configuration;
using API.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace API.Tests;

/// <summary>
/// Matching has to be forgiving about how Whisper writes a phrase down and unforgiving
/// about what counts as being addressed. A false negative is a trigger you have to press
/// the button for; a false positive is Bubbles talking over a host.
/// </summary>
public class TriggerPhraseMatcherTests
{
    private static TriggerPhraseMatcher Matcher(params string[] phrases) =>
        new(NullLogger<TriggerPhraseMatcher>.Instance,
            Options.Create(new TriggerPhraseOptions { Phrases = [.. phrases] }));

    [Theory]
    [InlineData("So anyway... Over to you, Bubbles!")]
    [InlineData("over to you bubbles")]
    [InlineData("OVER TO YOU, BUBBLES.")]
    [InlineData("I just want to know if you're happy to be on this panel. Over to you bubbles.")]
    public void Finds_the_phrase_whatever_the_punctuation_and_casing(string heard) =>
        Assert.Equal("over to you bubbles", Matcher().FindPhrase(heard));

    [Theory]
    [InlineData("I think Bubbles would disagree with that")]       // third person
    [InlineData("what do you think, Jason?")]                       // host to host
    [InlineData("over to you bubblesworth")]                        // whole words only
    [InlineData("over to you, everyone")]                           // no name
    public void Does_not_fire_on_things_that_are_not_being_addressed_to_Bubbles(string heard) =>
        Assert.Null(Matcher().FindPhrase(heard));

    [Fact]
    public void Configured_phrases_replace_the_defaults()
    {
        var matcher = Matcher("Righto, Bubbles");

        Assert.Equal(["Righto, Bubbles"], matcher.Phrases);
        Assert.NotNull(matcher.FindPhrase("righto bubbles"));
        Assert.Null(matcher.FindPhrase("over to you bubbles"));
    }

    [Fact]
    public void One_utterance_fires_once_even_when_two_devices_hear_it()
    {
        var matcher = Matcher();
        var fired = 0;
        matcher.TriggerDetected += (_, _) => fired++;

        Assert.True(matcher.TryMatch("over to you bubbles", "mic-1"));
        Assert.True(matcher.TryMatch("over to you bubbles", "mic-2"));

        // Both matched - so both devices mark the audio consumed - but only one response.
        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData("What's  your-take, Bubbles?", "whats your take bubbles")]
    [InlineData("Over to you, Bubbles!!!", "over to you bubbles")]
    [InlineData("  spaced   out  ", "spaced out")]
    public void Normalisation_strips_everything_that_is_not_a_word(string input, string expected) =>
        Assert.Equal(expected, TriggerPhraseMatcher.Normalise(input));
}
