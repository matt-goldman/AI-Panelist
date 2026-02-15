namespace API.Services;

/// <summary>
/// Manages a rolling transcript buffer with time-based expiration
/// </summary>
public class TranscriptBufferService(TimeSpan bufferDuration)
{
    private readonly List<TranscriptEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// Add a new transcript entry
    /// </summary>
    public void AddEntry(string text, DateTime timestamp, string? speakerName = null)
    {
        lock (_lock)
        {
            _entries.Add(new TranscriptEntry
            {
                Text = text,
                Timestamp = timestamp,
                SpeakerName = speakerName
            });

            // Remove expired entries
            CleanupOldEntries();
        }
    }

    /// <summary>
    /// Get all transcript text within the buffer window (with speaker attribution)
    /// </summary>
    public string GetFullTranscript()
    {
        lock (_lock)
        {
            CleanupOldEntries();
            return FormatTranscript(_entries);
        }
    }

    /// <summary>
    /// Get recent transcript (last N seconds) with speaker attribution
    /// </summary>
    public string GetRecentTranscript(TimeSpan recentWindow)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - recentWindow;
            var recentEntries = _entries.Where(e => e.Timestamp >= cutoff).ToList();
            return FormatTranscript(recentEntries);
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

    private static string FormatTranscript(List<TranscriptEntry> entries)
    {
        if (entries.Count == 0)
            return string.Empty;

        // Group consecutive entries by speaker to reduce noise
        var formattedParts = new List<string>();
        string? currentSpeaker = null;
        var currentTexts = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.SpeakerName != currentSpeaker && currentTexts.Count > 0)
            {
                // Flush current speaker's text
                formattedParts.Add(FormatSpeakerBlock(currentSpeaker, currentTexts));
                currentTexts.Clear();
            }

            currentSpeaker = entry.SpeakerName;
            currentTexts.Add(entry.Text);
        }

        // Flush remaining
        if (currentTexts.Count > 0)
        {
            formattedParts.Add(FormatSpeakerBlock(currentSpeaker, currentTexts));
        }

        return string.Join("\n", formattedParts);
    }

    private static string FormatSpeakerBlock(string? speakerName, List<string> texts)
    {
        var combinedText = string.Join(" ", texts);
        
        if (string.IsNullOrWhiteSpace(speakerName))
            return combinedText;
        
        return $"[{speakerName}]: {combinedText}";
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow - bufferDuration;
        _entries.RemoveAll(e => e.Timestamp < cutoff);
    }

    private class TranscriptEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? SpeakerName { get; set; }
    }
}
