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
    /// Builds a response generation prompt using the summary and recent transcript
    /// </summary>
    public string BuildResponsePrompt(string summary, string recentTranscript)
    {
        return _config.ResponsePromptTemplate
            .Replace("{systemPrompt}", _config.SystemPrompt)
            .Replace("{summary}", summary)
            .Replace("{recentTranscript}", recentTranscript)
            .Replace("{maxWords}", _config.MaxResponseWords.ToString());
    }
}
