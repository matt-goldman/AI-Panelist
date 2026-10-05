using System.Runtime.CompilerServices;

namespace API.Services.Interfaces;

/// <summary>
/// Interface for language model inference services
/// </summary>
public interface ILanguageModelService
{
    /// <summary>
    /// Generate a summary of the provided transcript
    /// </summary>
    Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a conversational response based on summary and recent transcript
    /// </summary>
    Task<string> GenerateResponseAsync(
        string summary,
        string recentTranscript,
        string question,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a conversational response as it is generated.
    ///
    /// This is what makes time-to-first-audio short: chunks are flushed to TTS at clause
    /// boundaries while the model is still writing the rest. Implementations must yield
    /// only <em>answer</em> text — if a model emits reasoning tokens first, streaming those
    /// would gate the first audio on the whole thinking phase, which is worse than not
    /// streaming at all.
    ///
    /// The default implementation falls back to generating the whole response and yielding
    /// it as a single chunk, so a non-streaming backend still works (just without the
    /// latency benefit).
    /// </summary>
    async IAsyncEnumerable<string> StreamResponseAsync(
        string summary,
        string recentTranscript,
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateResponseAsync(summary, recentTranscript, question, cancellationToken);
    }

    /// <summary>
    /// Whether this implementation streams for real, rather than relying on the
    /// single-chunk fallback above. Used only for logging an accurate startup summary.
    /// </summary>
    bool SupportsStreaming => false;
}

/// <summary>
/// A response stream ended without any answer text — typically a reasoning model that
/// spent its whole output budget thinking. Thrown at the end of the stream rather than
/// returning quietly, so the caller can say something instead of going silent.
/// </summary>
public sealed class NoAnswerException(string message) : Exception(message);
