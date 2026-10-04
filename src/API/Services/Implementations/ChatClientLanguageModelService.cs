using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

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
///
/// With transcript search on, responses (never summaries) are offered a
/// search_panel_transcript tool, and go through a function-invoking client that runs it
/// and hands the result back. Off, the response path is exactly the plain client.
/// </summary>
public class ChatClientLanguageModelService : ILanguageModelService
{
    private const string SearchToolName = "search_panel_transcript";

    private static readonly ChatOptions SummaryOptions = new()
    {
        Temperature     = 0.3f, // Focused and deterministic for summarization
        TopP            = 0.8f,
        MaxOutputTokens = 500
    };

    private readonly ILogger<ChatClientLanguageModelService> _logger;
    private readonly IChatClient _chatClient;
    private readonly IChatClient _responseClient;
    private readonly PromptService _promptService;
    private readonly PanelTranscriptLog _transcriptLog;
    private readonly TranscriptSearchOptions _searchOptions;

    public ChatClientLanguageModelService(
        ILogger<ChatClientLanguageModelService> logger,
        ILoggerFactory loggerFactory,
        IChatClient chatClient,
        PromptService promptService,
        PanelTranscriptLog transcriptLog,
        IOptions<TranscriptSearchOptions> searchOptions)
    {
        _logger = logger;
        _chatClient = chatClient;
        _promptService = promptService;
        _transcriptLog = transcriptLog;
        _searchOptions = searchOptions.Value;

        // The searches-per-response limit is enforced by the tool itself (see
        // CreateResponseOptions), so it can tell the model to answer rather than leave it
        // with an unanswered call. The iteration cap is only a backstop on top of that.
        _responseClient = _searchOptions.Enabled
            ? chatClient.AsBuilder()
                .UseFunctionInvocation(loggerFactory, c => c.MaximumIterationsPerRequest = _searchOptions.MaxSearchesPerResponse + 2)
                .Build()
            : chatClient;
    }

    public bool SupportsStreaming => true;

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating summary for {Length} character transcript", transcript.Length);

        var prompt = _promptService.BuildSummarizationPrompt(transcript);
        var response = await _chatClient.GetResponseAsync(prompt, SummaryOptions, cancellationToken);

        var summary = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        _logger.LogDebug("Generated summary: {Summary}", summary);

        return summary;
    }

    public async Task<string> GenerateResponseAsync(
        string summary,
        string recentTranscript,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating response (non-streaming)");

        var response = await _responseClient.GetResponseAsync(
            BuildResponseMessages(summary, recentTranscript), CreateResponseOptions(), cancellationToken);

        var text = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        _logger.LogDebug("Generated response: {Response}", text);

        return text;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string summary,
        string recentTranscript,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Streaming response");

        var messages = BuildResponseMessages(summary, recentTranscript);
        var reasoningCharacters = 0;
        var answerCharacters = 0;
        var loggedReasoning = false;

        await foreach (var update in _responseClient.GetStreamingResponseAsync(messages, CreateResponseOptions(), cancellationToken))
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
                            _logger.LogDebug("Model is emitting reasoning tokens; holding audio until the answer starts");
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

        _logger.LogInformation(
            "Streaming complete: {Answer} characters of answer, {Reasoning} characters of reasoning discarded",
            answerCharacters, reasoningCharacters);
    }

    private List<ChatMessage> BuildResponseMessages(string summary, string recentTranscript)
    {
        var messages = new List<ChatMessage>();

        if (_searchOptions.Enabled && !string.IsNullOrWhiteSpace(_searchOptions.Guidance))
        {
            messages.Add(new ChatMessage(ChatRole.System, _searchOptions.Guidance));
        }

        messages.Add(new ChatMessage(ChatRole.User, _promptService.BuildResponsePrompt(summary, recentTranscript)));
        return messages;
    }

    /// <summary>
    /// Fresh per response, because the search tool counts its own uses.
    /// </summary>
    private ChatOptions CreateResponseOptions()
    {
        var options = new ChatOptions
        {
            Temperature     = 0.7f, // More varied for responses
            TopP            = 0.9f,
            MaxOutputTokens = 800   // Headroom over the 150 word target
        };

        if (_searchOptions.Enabled)
        {
            var searches = 0;

            options.Tools =
            [
                AIFunctionFactory.Create(
                    ([Description("A few distinctive words for what you're looking for - a topic, name or term, not a whole sentence.")]
                        string keywords) =>
                    {
                        if (++searches > _searchOptions.MaxSearchesPerResponse)
                        {
                            _logger.LogWarning("Model asked for search {Count}; the limit is {Limit}", searches, _searchOptions.MaxSearchesPerResponse);
                            return "No more searches for this answer. Answer now with what you have.";
                        }

                        return SearchTranscript(keywords);
                    },
                    SearchToolName,
                    "Search everything said so far in this panel discussion, including your own earlier answers. "
                    + "Keyword search over a speech-to-text transcript: matches words, not meaning.")
            ];
        }

        return options;
    }

    private string SearchTranscript(string keywords)
    {
        var stopwatch = Stopwatch.StartNew();
        var terms = PanelTranscriptLog.Terms(keywords);
        var hits = _transcriptLog.Search(keywords, _searchOptions.MaxResults, _searchOptions.ContextEntries);
        var result = PanelTranscriptLog.Format(terms, hits, DateTime.UtcNow);

        // Every search costs a model round trip before any audio. Log enough to see both
        // why it matched and what it cost.
        _logger.LogInformation(
            "Transcript search for '{Keywords}' (terms: {Terms}) found {Hits} of {Entries} entries in {Ms}ms",
            keywords, string.Join(", ", terms), hits.Count, _transcriptLog.Count, stopwatch.ElapsedMilliseconds);
        foreach (var hit in hits)
        {
            _logger.LogDebug("  hit {Time:HH:mm:ss} score {Score} matched [{Matched}]",
                hit.TimestampUtc.ToLocalTime(), hit.Score, string.Join(", ", hit.MatchedTerms));
        }

        return result;
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
