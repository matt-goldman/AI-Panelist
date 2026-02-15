using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of language model service for testing
/// </summary>
public class MockLanguageModelService : ILanguageModelService
{
    private readonly ILogger<MockLanguageModelService> _logger;

    public MockLanguageModelService(ILogger<MockLanguageModelService> logger)
    {
        _logger = logger;
    }

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock LLM: Generating summary for {Length} character transcript", transcript.Length);

        // Simulate processing time
        await Task.Delay(500, cancellationToken);

        var summary = @"• Mock summary point about technology discussion
• Key themes identified in the conversation
• Points of agreement and disagreement noted
• Questions raised during the panel
• Recent developments mentioned";

        _logger.LogDebug("Mock LLM: Generated summary with {Length} characters", summary.Length);
        return summary;
    }

    public async Task<string> GenerateResponseAsync(string summary, string recentTranscript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock LLM: Generating response based on summary and recent transcript");

        // Simulate LLM processing time
        await Task.Delay(2000, cancellationToken);

        var response = "That's a fascinating point. Based on what I've heard, I think there's merit to both perspectives being discussed. " +
                      "The key consideration here is finding the right balance between innovation and pragmatism. " +
                      "I appreciate the thoughtful discussion and would be curious to hear more about the practical implications.";

        _logger.LogDebug("Mock LLM: Generated response with {Length} characters", response.Length);
        return response;
    }
}
