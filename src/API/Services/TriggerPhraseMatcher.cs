using System.Text;
using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Decides whether a piece of transcribed text contains a spoken trigger phrase, and
/// raises <see cref="TriggerDetected"/> when it does.
///
/// Deliberately plain: normalise both sides, whole-word substring match. No fuzzy
/// matching until testing shows Whisper mangling the specific phrases — fuzziness buys
/// recall at the cost of false fires, and a false fire is Bubbles talking over a host.
///
/// Kept separate from any audio so that other sources (the moderator phone's on-device
/// STT, say) can feed it text later without touching the Whisper path.
/// </summary>
public sealed class TriggerPhraseMatcher
{
    private readonly ILogger<TriggerPhraseMatcher> _logger;
    private readonly TriggerPhraseOptions _options;
    private readonly (string Phrase, string Normalised)[] _phrases;
    private readonly Lock _sync = new();
    private DateTime _lastFiredUtc = DateTime.MinValue;

    public TriggerPhraseMatcher(ILogger<TriggerPhraseMatcher> logger, IOptions<TriggerPhraseOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        var configured = _options.Phrases.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _phrases = (configured.Count > 0 ? configured : [.. TriggerPhraseOptions.DefaultPhrases])
            .Select(p => (p, Normalise(p)))
            .Where(p => p.Item2.Length > 0)
            .ToArray();

        foreach (var (phrase, normalised) in _phrases)
        {
            if (!$" {normalised} ".Contains(" bubbles "))
            {
                _logger.LogWarning(
                    "Trigger phrase '{Phrase}' doesn't mention Bubbles - it will fire on ordinary host-to-host conversation",
                    phrase);
            }
        }
    }

    public IReadOnlyList<string> Phrases => _phrases.Select(p => p.Phrase).ToList();

    public event EventHandler<TriggerPhraseDetectedEventArgs>? TriggerDetected;

    /// <summary>
    /// The first configured phrase found in <paramref name="text"/>, or null. No side effects.
    /// </summary>
    public string? FindPhrase(string text)
    {
        var haystack = $" {Normalise(text)} ";

        foreach (var (phrase, normalised) in _phrases)
        {
            if (haystack.Contains($" {normalised} ", StringComparison.Ordinal))
            {
                return phrase;
            }
        }

        return null;
    }

    /// <summary>
    /// Check text for a trigger phrase and fire if one is found and we're not cooling
    /// down from a previous fire. Returns true whenever a phrase matched — even inside
    /// the cooldown — so the caller can mark that audio consumed either way.
    /// </summary>
    public bool TryMatch(string text, string? source)
    {
        var phrase = FindPhrase(text);
        if (phrase is null) return false;

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (now - _lastFiredUtc < TimeSpan.FromMilliseconds(_options.CooldownMs))
            {
                _logger.LogDebug("Trigger phrase '{Phrase}' from {Source} ignored - within cooldown", phrase, source);
                return true;
            }

            _lastFiredUtc = now;
        }

        _logger.LogInformation("Spoken trigger '{Phrase}' heard from {Source}: {Text}", phrase, source, text);
        TriggerDetected?.Invoke(this, new TriggerPhraseDetectedEventArgs(phrase, text, source));
        return true;
    }

    /// <summary>
    /// Lowercase, letters and digits only, single spaces. Apostrophes are removed rather
    /// than spaced so "what's" and "whats" agree.
    /// </summary>
    public static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '’')
            {
                // Join the word back up.
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }
}

public sealed record TriggerPhraseDetectedEventArgs(string Phrase, string Text, string? Source);
