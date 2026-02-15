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
    Task<string> GenerateResponseAsync(string summary, string recentTranscript, CancellationToken cancellationToken = default);
}
