using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Service for building prompts used by language model services
/// </summary>
public class PromptService
{
    private readonly PromptConfiguration _config;

    public PromptService(IOptions<PromptConfiguration> options)
    {
        _config = options.Value;
    }

    /// <summary>
    /// Gets the system prompt that defines the AI panelist's personality
    /// </summary>
    public string SystemPrompt => _config.SystemPrompt;

    /// <summary>
    /// Builds a summarization prompt for the given transcript
    /// </summary>
    public string BuildSummarizationPrompt(string transcript)
    {
        return _config.SummarizationPromptTemplate
            .Replace("{transcript}", transcript);
    }

    /// <summary>
    /// Builds a response generation prompt.
    /// </summary>
    /// <param name="question">
    /// What has been said since Bubbles last spoke. Without this the model sees a minute of
    /// transcript containing several questions, no record of which it has already answered
    /// — its own replies are kept out of the captured audio by self-suppression — and picks
    /// one more or less at random. In testing it answered the previous question while the
    /// new one sat two lines below.
    /// </param>
    public string BuildResponsePrompt(string summary, string recentTranscript, string question = "")
    {
        return _config.ResponsePromptTemplate
            .Replace("{systemPrompt}", _config.SystemPrompt)
            .Replace("{summary}", string.IsNullOrWhiteSpace(summary) ? "(none yet)" : summary)
            .Replace("{recentTranscript}", recentTranscript)
            .Replace("{question}", string.IsNullOrWhiteSpace(question)
                ? "(nothing new since you last spoke - respond to the most recent thing above)"
                : question)
            .Replace("{maxWords}", _config.MaxResponseWords.ToString());
    }
}
