using API.Services.Interfaces;
using Azure.AI.Inference;

namespace API.Services.Implementations;

/// <summary>
/// Language model service using Azure AI Foundry Local (local models)
/// </summary>
public class FoundryLocalLanguageModelService : ILanguageModelService
{
    private readonly ChatCompletionsClient _client;
    private readonly PromptService _promptService;
    private readonly ILogger<FoundryLocalLanguageModelService> _logger;

    public FoundryLocalLanguageModelService(
        ChatCompletionsClient client,
        PromptService promptService,
        ILogger<FoundryLocalLanguageModelService> logger)
    {
        _client = client;
        _promptService = promptService;
        _logger = logger;

        _logger.LogInformation("Foundry Local language model service initialized");
    }

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating summary via Foundry Local for {Length} character transcript", transcript.Length);

        var prompt = _promptService.BuildSummarizationPrompt(transcript);

        try
        {
            var requestOptions = new ChatCompletionsOptions
            {
                Temperature             = 0.3f, // More focused and deterministic for summarization
                NucleusSamplingFactor   = 0.8f,
                MaxTokens               = 500,
                Model                   = "gpt-oss-20b-cuda-gpu", // Specify the local model to use
                Messages                =
                {
                    new ChatRequestUserMessage(prompt)
                }
            };

            var response = await _client.CompleteAsync(requestOptions, cancellationToken);

            var result = response.Value.Content;
            _logger.LogDebug("Generated summary: {Summary}", result);

            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating summary via Foundry Local");
            throw;
        }
    }

    public async Task<string> GenerateResponseAsync(
        string summary,
        string recentTranscript,
        string question,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating response via Foundry Local");

        try
        {
            var requestOptions = new ChatCompletionsOptions
            {
                Temperature             = 0.7f, // More creative for responses
                NucleusSamplingFactor   = 0.9f,
                MaxTokens               = 500,
                Model                   = "gpt-oss-20b-cuda-gpu", // Specify the local model to use
                Messages                =
                {
                    new ChatRequestSystemMessage(_promptService.SystemPrompt),
                    new ChatRequestUserMessage($"""
                        Current discussion summary:
                        {summary}

                        Recent transcript excerpt:
                        {recentTranscript}

                        Generate a conversational response (≤150 words):
                        """)
                }
            };

            var response = await _client.CompleteAsync(requestOptions, cancellationToken);

            var result = response.Value.Content;
            _logger.LogDebug("Generated response: {Response}", result);

            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response via Foundry Local");
            throw;
        }
    }
}
