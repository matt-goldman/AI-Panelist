namespace API.Services;

/// <summary>
/// Manages a rolling transcript buffer with time-based expiration
/// </summary>
public class TranscriptBufferService
{
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();
    private readonly TimeSpan _bufferDuration;

    public TranscriptBufferService(TimeSpan bufferDuration)
    {
        _bufferDuration = bufferDuration;
    }

    /// <summary>
    /// Add a new transcript entry
    /// </summary>
    public void AddEntry(string text, DateTime timestamp)
    {
        lock (_lock)
        {
            _entries.Add(new TranscriptEntry
            {
                Text = text,
                Timestamp = timestamp
            });

            // Remove expired entries
            CleanupOldEntries();
        }
    }

    /// <summary>
    /// Get all transcript text within the buffer window
    /// </summary>
    public string GetFullTranscript()
    {
        lock (_lock)
        {
            CleanupOldEntries();
            return string.Join(" ", _entries.Select(e => e.Text));
        }
    }

    /// <summary>
    /// Get recent transcript (last N seconds)
    /// </summary>
    public string GetRecentTranscript(TimeSpan recentWindow)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - recentWindow;
            var recentEntries = _entries.Where(e => e.Timestamp >= cutoff).ToList();
            return string.Join(" ", recentEntries.Select(e => e.Text));
        }
    }

    /// <summary>
    /// Clear all transcript entries
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow - _bufferDuration;
        _entries.RemoveAll(e => e.Timestamp < cutoff);
    }

    private class TranscriptEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
