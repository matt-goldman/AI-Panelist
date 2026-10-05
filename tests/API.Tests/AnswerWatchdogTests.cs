using API.Services;
using API.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace API.Tests;

/// <summary>
/// The never-go-silent guarantee depends on every way a response can fail to produce
/// words ending the stream cleanly rather than throwing — so whatever is already queued
/// still plays, and a canned line follows it.
/// </summary>
public class AnswerWatchdogTests
{
    private static async IAsyncEnumerable<string> Stream(
        IEnumerable<(int DelayMs, string? Token, Exception? Throw)> steps,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (delay, token, throws) in steps)
        {
            await Task.Delay(delay, cancellationToken);
            if (throws is not null) throw throws;
            yield return token!;
        }
    }

    private static async Task<(string Text, string? Failure)> Drain(
        TimeSpan maxSilence,
        IEnumerable<(int, string?, Exception?)> steps,
        int consumerDelayMs = 0,
        CancellationToken cancellationToken = default)
    {
        using var watchdog = new AnswerWatchdog(maxSilence, NullLogger.Instance);
        var text = new System.Text.StringBuilder();

        await foreach (var token in watchdog.Watch(ct => Stream(steps, ct), cancellationToken))
        {
            text.Append(token);
            if (consumerDelayMs > 0) await Task.Delay(consumerDelayMs, CancellationToken.None);
        }

        return (text.ToString(), watchdog.Failure);
    }

    [Fact]
    public async Task Passes_a_normal_stream_through_untouched()
    {
        var (text, failure) = await Drain(TimeSpan.FromMilliseconds(300), [(50, "Hello ", null), (50, "there.", null)]);

        Assert.Equal("Hello there.", text);
        Assert.Null(failure);
    }

    [Fact]
    public async Task A_model_that_stalls_ends_the_stream_but_keeps_what_was_already_said()
    {
        var (text, failure) = await Drain(TimeSpan.FromMilliseconds(300), [(50, "Hold on. ", null), (2000, "too late", null)]);

        Assert.Equal("Hold on. ", text);
        Assert.StartsWith("no answer text for", failure);
    }

    [Fact]
    public async Task Time_spent_playing_audio_is_not_counted_as_silence()
    {
        // The pipeline stops pulling tokens while a chunk plays. That is not the model
        // being slow, and treating it as such would cut off every long answer.
        var (text, failure) = await Drain(
            TimeSpan.FromMilliseconds(300),
            [(10, "a", null), (10, "b", null), (10, "c", null)],
            consumerDelayMs: 800);

        Assert.Equal("abc", text);
        Assert.Null(failure);
    }

    [Fact]
    public async Task An_empty_answer_becomes_a_failure_rather_than_a_finished_response()
    {
        var (_, failure) = await Drain(TimeSpan.FromSeconds(5), [(10, null, new NoAnswerException("spent it all reasoning"))]);

        Assert.Equal("spent it all reasoning", failure);
    }

    [Fact]
    public async Task An_error_from_the_model_becomes_a_failure()
    {
        var (text, failure) = await Drain(
            TimeSpan.FromSeconds(5),
            [(10, "x", null), (10, null, new HttpRequestException("inference server down"))]);

        Assert.Equal("x", text);
        Assert.Contains("inference server down", failure);
    }

    [Fact]
    public async Task A_moderator_cancel_still_surfaces_as_cancellation()
    {
        // Must not look like "no answer", or cancelling would earn a canned quip.
        using var cancel = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Drain(TimeSpan.FromSeconds(5), [(2000, "never", null)], cancellationToken: cancel.Token));
    }
}
