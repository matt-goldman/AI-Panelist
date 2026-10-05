using System.Collections.Concurrent;

namespace API.Services;

/// <summary>
/// Which displays are connected to the hub.
///
/// On the night, "is the tablet actually connected?" is answered by walking over and
/// looking at it. It is the same class of problem as the backlog's item 8a — the operator
/// is the last to find out — and the hub already knows the answer.
/// </summary>
public sealed class DisplayRegistry
{
    private readonly ConcurrentDictionary<string, DateTime> _connections = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>
    /// How long the API has been up. A connection that is seconds old means something
    /// different ten minutes in than it does right after startup, where everything is new.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;

    public void Add(string connectionId) => _connections[connectionId] = DateTime.UtcNow;

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int Count => _connections.Count;

    /// <summary>
    /// How long each connected display has been attached. Long-lived connections are the
    /// healthy case; one that keeps resetting is a display that keeps dropping.
    /// </summary>
    public IReadOnlyList<double> ConnectedSeconds
    {
        get
        {
            var now = DateTime.UtcNow;
            return _connections.Values.Select(since => (now - since).TotalSeconds).OrderBy(s => s).ToList();
        }
    }
}
