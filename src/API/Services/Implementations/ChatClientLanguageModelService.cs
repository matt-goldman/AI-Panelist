using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

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
///
/// With triage on, a streamed response is answered off the cuff with reasoning off, unless
/// the model asks for thinking time — see <see cref="ResponseTriageOptions"/>.
/// </summary>
public class ChatClientLanguageModelService : ILanguageModelService
{
    private const string SearchToolName = "search_panel_transcript";

    /// <summary>
    /// DI key for the client summaries run on. Usually the same instance as the response
    /// client; pointed somewhere else, summarising stops competing with answering.
    /// </summary>
    public const string SummaryClientKey = "summary";

    // Reasoning off. Summarising a transcript into bullets does not need a chain of
    // thought, and every reasoning token is GPU time a response might be queued behind.
    private static readonly ChatOptions SummaryOptions = new ChatOptions
    {
        Temperature     = 0.3f, // Focused and deterministic for summarization
        TopP            = 0.8f,
        MaxOutputTokens = 500
    }.AddOllamaOption(OllamaOption.Think, false);

    private readonly ILogger<ChatClientLanguageModelService> _logger;
    private readonly IChatClient _chatClient;
    private readonly IChatClient _summaryClient;
    private readonly IChatClient _responseClient;
    private readonly PromptService _promptService;
    private readonly PanelTranscriptLog _transcriptLog;
    private readonly TranscriptSearchOptions _searchOptions;
    private readonly ResponseTriageOptions _triageOptions;

    public ChatClientLanguageModelService(
        ILogger<ChatClientLanguageModelService> logger,
        ILoggerFactory loggerFactory,
        IChatClient chatClient,
        [FromKeyedServices(SummaryClientKey)] IChatClient summaryClient,
        PromptService promptService,
        PanelTranscriptLog transcriptLog,
        IOptions<TranscriptSearchOptions> searchOptions,
        IOptions<ResponseTriageOptions> triageOptions)
    {
        _logger = logger;
        _chatClient = chatClient;
        _summaryClient = summaryClient;
        _promptService = promptService;
        _transcriptLog = transcriptLog;
        _searchOptions = searchOptions.Value;
        _triageOptions = triageOptions.Value;

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
        var response = await _summaryClient.GetResponseAsync(prompt, SummaryOptions, cancellationToken);

        var summary = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        _logger.LogDebug("Generated summary: {Summary}", summary);

        return summary;
    }

    public async Task<string> GenerateResponseAsync(
        string summary,
        string recentTranscript,
        string question,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating response (non-streaming)");

        var response = await _responseClient.GetResponseAsync(
            BuildResponseMessages(summary, recentTranscript, question, triage: false),
            CreateResponseOptions(CreateSearchTools()),
            cancellationToken);

        var text = ExtractAnswerText(response.Messages.SelectMany(m => m.Contents));
        _logger.LogDebug("Generated response: {Response}", text);

        return text;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string summary,
        string recentTranscript,
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Shared by both triage passes, so the search limit is per answer, not per pass.
        var tools = CreateSearchTools();

        if (!_triageOptions.Enabled)
        {
            _logger.LogInformation("Streaming response");

            await foreach (var text in StreamPassAsync(
                BuildResponseMessages(summary, recentTranscript, question, triage: false),
                CreateResponseOptions(tools), "response", cancellationToken))
            {
                yield return text;
            }

            yield break;
        }

        _logger.LogInformation("Streaming response, triage pass (reasoning off)");

        var marker = _triageOptions.Marker;
        var head = new StringBuilder();
        string? holdingLine = null;

        // Scoped so the triage request is closed - and the GPU freed - before the thinking
        // pass starts, rather than when this whole method finishes.
        {
            await using var triage = StreamPassAsync(
                    BuildResponseMessages(summary, recentTranscript, question, triage: true),
                    CreateResponseOptions(tools, think: false), "triage", cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            // Hold the first few characters back until we know whether they're the marker.
            bool? deferred = null;
            while (deferred is null && await triage.MoveNextAsync())
            {
                head.Append(triage.Current);
                var text = head.ToString().TrimStart();

                if (text.Length >= marker.Length)
                {
                    deferred = text.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
                }
                else if (!marker.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                {
                    deferred = false;
                }
            }

            if (deferred != true)
            {
                // The common case: answered off the cuff. Stream the rest as normal.
                _logger.LogInformation("Triage: answering directly");
                yield return head.ToString();

                while (await triage.MoveNextAsync())
                {
                    yield return triage.Current;
                }

                yield break;
            }

            // Asked for thinking time. Read just the holding line, then stop the request.
            while (!EndsHoldingLine(head) && await triage.MoveNextAsync())
            {
                head.Append(triage.Current);
            }

            holdingLine = CleanHoldingLine(head.ToString().TrimStart()[marker.Length..]);
        }

        _logger.LogInformation("Triage: needs thought. Holding line: {HoldingLine}", holdingLine);

        // Spoken now, while the thinking pass runs.
        yield return holdingLine;
        yield return ResponseChunker.Flush;

        var messages = BuildResponseMessages(summary, recentTranscript, question, triage: false);
        messages.Add(new ChatMessage(ChatRole.Assistant, holdingLine));
        messages.Add(new ChatMessage(ChatRole.User, _triageOptions.ContinuePrompt));

        // Told not to, the model still tends to open by repeating the holding line, which
        // would be heard twice. Hold the start back until it can be compared.
        var holding = $" {TriggerPhraseMatcher.Normalise(holdingLine)} ";
        var start = new StringBuilder();
        var checkedStart = false;

        await foreach (var text in ThinkOrFallBackAsync(messages, tools, cancellationToken))
        {
            if (checkedStart)
            {
                yield return text;
                continue;
            }

            start.Append(text);
            if (start.Length < holdingLine.Length + 1) continue;

            checkedStart = true;
            yield return StripRepeatedHoldingLine(start.ToString(), holding);
        }

        if (!checkedStart && start.Length > 0)
        {
            yield return StripRepeatedHoldingLine(start.ToString(), holding);
        }
    }

    /// <summary>
    /// The thinking pass, with a time limit on how long it may reason before answering.
    /// On this hardware a reasoning model can think for 30 seconds and still run out of
    /// budget before saying anything. If it hasn't started answering in time, or produces
    /// nothing, ask again with reasoning off for a best-effort answer: an approximate answer
    /// a second later beats a canned quip.
    /// </summary>
    private async IAsyncEnumerable<string> ThinkOrFallBackAsync(
        List<ChatMessage> messages,
        IList<AITool>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? failure = null;

        using (var thinkingTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (_triageOptions.ThinkingTimeoutSeconds > 0)
            {
                thinkingTimer.CancelAfter(TimeSpan.FromSeconds(_triageOptions.ThinkingTimeoutSeconds));
            }

            await using var thinking = StreamPassAsync(
                    messages,
                    CreateResponseOptions(tools, think: true, _triageOptions.ThinkingMaxOutputTokens),
                    "thinking", thinkingTimer.Token)
                .GetAsyncEnumerator(thinkingTimer.Token);

            var answering = false;
            while (true)
            {
                bool hasText;
                try
                {
                    hasText = await thinking.MoveNextAsync();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !answering)
                {
                    failure = $"no answer after {_triageOptions.ThinkingTimeoutSeconds}s of thinking";
                    break;
                }
                catch (NoAnswerException ex)
                {
                    failure = ex.Message;
                    break;
                }

                if (!hasText) break;

                if (!answering)
                {
                    // It's answering: the time limit was on thinking, not on talking.
                    answering = true;
                    thinkingTimer.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                yield return thinking.Current;
            }
        }

        if (failure is null) yield break;

        _logger.LogWarning("Thinking pass gave up ({Reason}); answering off the cuff instead", failure);

        messages.Add(new ChatMessage(ChatRole.User, _triageOptions.GiveUpPrompt));
        await foreach (var text in StreamPassAsync(
            messages, CreateResponseOptions(tools, think: false), "give-up", cancellationToken))
        {
            yield return text;
        }
    }

    /// <summary>
    /// Drop the holding line from the start of the answer if the model has said it again.
    /// </summary>
    private static string StripRepeatedHoldingLine(string text, string normalisedHolding)
    {
        // Find the shortest prefix that normalises to the holding line; cut there.
        for (var i = 1; i <= text.Length; i++)
        {
            var prefix = $" {TriggerPhraseMatcher.Normalise(text[..i])} ";
            if (prefix == normalisedHolding)
            {
                // Take any trailing punctuation that belongs to the repeated line with it.
                while (i < text.Length && !char.IsLetterOrDigit(text[i]) && !char.IsWhiteSpace(text[i])) i++;
                return text[i..].TrimStart();
            }

            if (!normalisedHolding.StartsWith(prefix.TrimEnd(), StringComparison.Ordinal)) break;
        }

        return text;
    }

    /// <summary>
    /// One request's answer text, with reasoning dropped. Throws
    /// <see cref="NoAnswerException"/> if it produced none, so silence is never mistaken
    /// for a finished answer.
    /// </summary>
    private async IAsyncEnumerable<string> StreamPassAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        string pass,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reasoningCharacters = 0;
        var answerCharacters = 0;
        var loggedReasoning = false;

        await foreach (var update in _responseClient.GetStreamingResponseAsync(messages, options, cancellationToken))
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
            "Streaming complete ({Pass}): {Answer} characters of answer, {Reasoning} characters of reasoning discarded",
            pass, answerCharacters, reasoningCharacters);

        // OllamaSharp ends a cancelled stream quietly rather than throwing. Without this, a
        // moderator cancel would look like an empty answer and earn a fallback line.
        cancellationToken.ThrowIfCancellationRequested();

        if (answerCharacters == 0)
        {
            throw new NoAnswerException(
                $"The {pass} pass produced no answer text ({reasoningCharacters} characters of reasoning, "
                + $"{options.MaxOutputTokens} token budget)");
        }
    }

    private bool EndsHoldingLine(StringBuilder head)
    {
        var text = head.ToString();
        var afterMarker = text.TrimStart().Length - _triageOptions.Marker.Length;

        return afterMarker >= _triageOptions.MaxHoldingLineChars
               || text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('!')
               || text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('…');
    }

    private string CleanHoldingLine(string text)
    {
        var line = text.Trim().Trim('"', '\'', '“', '”').Trim();

        if (line.Length > _triageOptions.MaxHoldingLineChars)
        {
            var cut = line.LastIndexOf(' ', _triageOptions.MaxHoldingLineChars);
            line = line[..(cut > 0 ? cut : _triageOptions.MaxHoldingLineChars)] + "...";
        }

        return line.Length > 0 ? line : _triageOptions.DefaultHoldingLine;
    }

    private List<ChatMessage> BuildResponseMessages(string summary, string recentTranscript, string question, bool triage)
    {
        var messages = new List<ChatMessage>();

        var system = new List<string>();
        if (_searchOptions.Enabled && !string.IsNullOrWhiteSpace(_searchOptions.Guidance))
        {
            system.Add(_searchOptions.Guidance);
        }

        if (system.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.System, string.Join("\n\n", system)));
        }

        var prompt = _promptService.BuildResponsePrompt(summary, recentTranscript, question);

        // Last in the prompt, not in a system message: behind the long panelist prompt a
        // system instruction was ignored outright in testing - the model never once asked
        // for thinking time, and ducked hard questions instead.
        if (triage)
        {
            prompt += "\n\n" + _triageOptions.Instruction.Replace("{marker}", _triageOptions.Marker);
        }

        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }

    /// <param name="think">
    /// Turn reasoning on or off for this request; null leaves the model's default. Sent as
    /// Ollama's "think" option - M.E.AI's provider-neutral ChatOptions.Reasoning is ignored
    /// by OllamaSharp, as of 5.4.
    /// </param>
    private static ChatOptions CreateResponseOptions(IList<AITool>? tools, bool? think = null, int maxOutputTokens = 800)
    {
        var options = new ChatOptions
        {
            Temperature     = 0.7f, // More varied for responses
            TopP            = 0.9f,
            MaxOutputTokens = maxOutputTokens, // 800 default: headroom over the 150 word target
            Tools           = tools
        };

        if (think is { } value)
        {
            options.AddOllamaOption(OllamaOption.Think, value);
        }

        return options;
    }

    /// <summary>
    /// Fresh per response, because the search tool counts its own uses.
    /// </summary>
    private IList<AITool>? CreateSearchTools()
    {
        if (!_searchOptions.Enabled) return null;

        var searches = 0;

        return
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
