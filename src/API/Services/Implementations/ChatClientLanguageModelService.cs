using System.Runtime.CompilerServices;
using System.Text;
using API.Services.Interfaces;
using Microsoft.Extensions.AI;

namespace API.Services.Implementations;

/// <summary>
/// Language model service over Microsoft.Extensions.AI's <see cref="IChatClient"/>.
///
/// One implementation for every backend: which provider is used is decided at runtime by
/// configuration (see Program.cs), so swapping local Ollama for a frontier model is a
/// config change, not a code change.
///
/// Streaming reads only the answer channel. M.E.AI surfaces a model's reasoning as
/// <see cref="TextReasoningContent"/>, distinct from <see cref="TextContent"/>, so
/// "chunk the answer, not the thinking" works the same way whatever the provider — which
/// matters because a model that streams its reasoning first would otherwise gate
/// time-to-first-audio on the entire thinking phase.
/// </summary>
public class ChatClientLanguageModelService(
    ILogger<ChatClientLanguageModelService> logger,
    IChatClient chatClient,
    PromptService promptService) : ILanguageModelService
{
    private static readonly ChatOptions SummaryOptions = new()
    {
        Temperature     = 0.3f, // Focused and deterministic for summarization
        TopP            = 0.8f,
        MaxOutputTokens = 500
    };

    private static readonly ChatOptions ResponseOptions = new()
    {
        Temperature     = 0.7f, // More varied for responses
        TopP            = 0.9f,
        MaxOutputTokens = 800   // Headroom over the 150 word target
    };

    public bool SupportsStreaming => true;

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating summary for {Length} character transcript", transcript.Length);

        var prompt = promptService.BuildSummarizationPrompt(transcript);
        var response = await chatClient.GetResponseAsync(prompt, SummaryOptions, cancellationToken);

        var summary = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        logger.LogDebug("Generated summary: {Summary}", summary);

        return summary;
    }

    public async Task<string> GenerateResponseAsync(
        string summary,
        string recentTranscript,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating response (non-streaming)");

        var prompt = promptService.BuildResponsePrompt(summary, recentTranscript);
        var response = await chatClient.GetResponseAsync(prompt, ResponseOptions, cancellationToken);

        var text = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        logger.LogDebug("Generated response: {Response}", text);

        return text;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string summary,
        string recentTranscript,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Streaming response");

        var prompt = promptService.BuildResponsePrompt(summary, recentTranscript);
        var reasoningCharacters = 0;
        var answerCharacters = 0;
        var loggedReasoning = false;

        await foreach (var update in chatClient.GetStreamingResponseAsync(prompt, ResponseOptions, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning:
                        // Deliberately not forwarded: this is the model thinking out loud.
                        // Surfacing it would be an avatar "thinking" state, never audio.
                        reasoningCharacters += reasoning.Text?.Length ?? 0;
                        if (!loggedReasoning)
                        {
                            logger.LogDebug("Model is emitting reasoning tokens; holding audio until the answer starts");
                            loggedReasoning = true;
                        }
                        break;

                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        answerCharacters += text.Text.Length;
                        yield return text.Text;
                        break;
                }
            }
        }

        logger.LogInformation(
            "Streaming complete: {Answer} characters of answer, {Reasoning} characters of reasoning discarded",
            answerCharacters, reasoningCharacters);
    }

    /// <summary>
    /// Pull the answer out of a completed response, leaving any reasoning behind.
    /// </summary>
    private static string ExtractAnswerText(IEnumerable<AIContent> contents)
    {
        var builder = new StringBuilder();

        foreach (var content in contents)
        {
            if (content is TextReasoningContent) continue;
            if (content is TextContent text) builder.Append(text.Text);
        }

        return builder.ToString().Trim();
    }
}
