using System.Runtime.CompilerServices;
using API.Services.Interfaces;

namespace API.Services;

/// <summary>
/// Wraps a response's token stream so that every way it can fail to produce an answer —
/// too long without answer text, an empty answer, an LLM error — ends the stream cleanly
/// instead of throwing, and records why in <see cref="Failure"/>.
///
/// Ending cleanly rather than throwing matters: the speech pipeline then drains whatever
/// is already queued (a holding line, half an answer) instead of cutting it off, and the
/// caller follows it with a fallback line.
/// </summary>
public sealed class AnswerWatchdog(TimeSpan maxSilence, ILogger logger) : IDisposable
{
    private CancellationTokenSource? _timer;

    /// <summary>
    /// Why the stream stopped early, or null if it completed normally.
    /// </summary>
    public string? Failure { get; private set; }

    /// <param name="source">Starts the stream. The token it's given is cancelled if the watchdog fires.</param>
    /// <param name="cancellationToken">The response's own cancellation (moderator cancel), which is passed through untouched.</param>
    public async IAsyncEnumerable<string> Watch(
        Func<CancellationToken, IAsyncEnumerable<string>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var tokens = source(_timer.Token).GetAsyncEnumerator(_timer.Token);

        while (true)
        {
            // Timed only while waiting on the model. Between tokens the speech pipeline may
            // hold off pulling for as long as a chunk takes to play, and that isn't silence.
            bool hasToken;
            try
            {
                Arm();
                hasToken = await tokens.MoveNextAsync();
                Disarm();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Failure = $"no answer text for {maxSilence.TotalSeconds:0.#}s";
                break;
            }
            catch (NoAnswerException ex)
            {
                Failure = ex.Message;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Response stream failed");
                Failure = $"{ex.GetType().Name}: {ex.Message}";
                break;
            }

            if (!hasToken) break;

            yield return tokens.Current;
        }
    }

    private void Arm()
    {
        if (maxSilence > TimeSpan.Zero)
        {
            _timer?.CancelAfter(maxSilence);
        }
    }

    private void Disarm() => _timer?.CancelAfter(Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer?.Dispose();
}
