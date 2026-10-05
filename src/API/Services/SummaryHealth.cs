namespace API.Services;

/// <summary>
/// Whether the rolling summary is actually being produced.
///
/// A failing summary is silent by design: the orchestrator catches the error, the previous
/// summary stays current, and the show goes on. That is the right behaviour and a terrible
/// diagnostic. When summaries have never succeeded the "previous summary" is empty, so
/// every response is generated with no sense of the discussion at all — and nothing says so.
///
/// It matters more now that summaries can run on a different endpoint from responses: that
/// endpoint can be wrong, unreachable or unauthorised while answers carry on working.
/// </summary>
public sealed class SummaryHealth
{
    private readonly Lock _sync = new();

    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;
    private string? _lastError;
    private int _consecutiveFailures;
    private int _successes;
    private int _abandoned;

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _lastSuccessUtc = DateTime.UtcNow;
            _consecutiveFailures = 0;
            _lastError = null;
            _successes++;
        }
    }

    public void RecordFailure(string error)
    {
        lock (_sync)
        {
            _lastFailureUtc = DateTime.UtcNow;
            _consecutiveFailures++;
            _lastError = error;
        }
    }

    /// <summary>
    /// Cancelled to let a response through. Deliberate, so not a failure — but worth
    /// counting, because summaries that are always abandoned never complete either.
    /// </summary>
    public void RecordAbandoned()
    {
        lock (_sync)
        {
            _abandoned++;
        }
    }

    public SummaryStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new SummaryStatus(
                    _successes, _consecutiveFailures, _abandoned,
                    _lastSuccessUtc, _lastFailureUtc, _lastError);
            }
        }
    }
}

public sealed record SummaryStatus(
    int Successes,
    int ConsecutiveFailures,
    int Abandoned,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string? LastError);
