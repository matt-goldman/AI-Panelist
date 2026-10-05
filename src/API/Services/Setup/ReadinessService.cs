using System.Diagnostics;
using API.Configuration;
using API.Services.Implementations;
using API.Services.Implementations.PipeWire;
using API.Services.Interfaces;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace API.Services.Setup;

/// <summary>
/// Serialised by name, because the setup app keys its styling off the status and a bare
/// number in the JSON would be a silent trap for anything else reading this.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
public enum CheckStatus
{
    /// <summary>Ready.</summary>
    Pass,

    /// <summary>Works, but not as intended — worth a look before going live.</summary>
    Warn,

    /// <summary>Will not work.</summary>
    Fail,

    /// <summary>Doesn't apply to how this run is configured.</summary>
    Skipped
}

/// <summary>
/// One thing that was checked.
/// </summary>
/// <param name="Remediation">What to do about it, in words, when it isn't ready.</param>
/// <param name="Action">
/// Id of a fix the setup app can run itself, from <see cref="SetupActions"/>. Null means
/// it needs a human.
/// </param>
public sealed record CheckResult(
    string Id,
    string Name,
    CheckStatus Status,
    string Detail,
    string? Remediation = null,
    string? Action = null,
    long ElapsedMs = 0);

public sealed record ReadinessReport(
    DateTime RanAtUtc,
    long ElapsedMs,
    bool Ready,
    IReadOnlyList<CheckResult> Checks)
{
    public int Failures => Checks.Count(c => c.Status == CheckStatus.Fail);
    public int Warnings => Checks.Count(c => c.Status == CheckStatus.Warn);
}

/// <summary>
/// Everything the run sheet used to check by hand, checked by software.
///
/// The principle from the backlog: move setup from discipline to automation. The run sheet
/// worked, but it was a checklist held together by memory and attention on the one night
/// attention is scarcest.
///
/// Checks that cost real time by hand are the ones here, and several of them double as
/// warm-ups — probing the model loads it, probing the TTS primes it — so running this is
/// useful even when everything passes.
/// </summary>
public sealed class ReadinessService(
    ILogger<ReadinessService> logger,
    IServiceProvider services,
    IAudioDeviceService deviceService,
    CaptureLevelMonitor levelMonitor,
    DisplayRegistry displays,
    SummaryHealth summaryHealth,
    FallbackSpeechService fallbackSpeech,
    ITextToSpeechService tts,
    ILanguageModelService languageModel,
    IConfiguration configuration,
    IOptions<AIPanelistOptions> panelistOptions,
    IOptions<PipeWireOptions> pipeWireOptions,
    IOptions<StreamingResponseOptions> streamingOptions,
    IOptions<TriggerPhraseOptions> triggerOptions,
    IOptions<TranscriptSearchOptions> searchOptions,
    IOptions<ResponseTriageOptions> triageOptions,
    IOptions<SilenceGuardOptions> silenceGuardOptions)
{
    private readonly AIPanelistOptions _panelist = panelistOptions.Value;
    private readonly PipeWireOptions _pipeWire = pipeWireOptions.Value;

    /// <summary>
    /// Run everything. Checks that can run at the same time do, because several of them
    /// wait on a model or a TTS server and the point is to be quick enough to re-run
    /// without thinking about it.
    /// </summary>
    public async Task<ReadinessReport> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Running readiness checks");

        var audio = AudioChecksAsync(cancellationToken);
        var model = TimedAsync("llm", "Language model", CheckLanguageModelAsync, cancellationToken);
        var summary = TimedAsync("summary-model", "Summary model", CheckSummaryModelAsync, cancellationToken);
        var speech = TimedAsync("tts", "Text to speech", CheckTtsAsync, cancellationToken);

        var checks = new List<CheckResult>();
        checks.AddRange(await audio);
        checks.Add(CheckWhisperModel());
        checks.Add(await model);
        checks.Add(await summary);
        checks.Add(await speech);
        checks.Add(CheckFallbackLines());
        checks.Add(CheckDisplays());
        checks.AddRange(CheckConfiguration());

        var report = new ReadinessReport(
            RanAtUtc:  DateTime.UtcNow,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            Ready:     checks.All(c => c.Status != CheckStatus.Fail),
            Checks:    checks);

        logger.LogInformation(
            "Readiness: {Status} - {Failures} failing, {Warnings} warning, in {Ms}ms",
            report.Ready ? "ready" : "NOT READY", report.Failures, report.Warnings, report.ElapsedMs);

        foreach (var check in checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.Warn))
        {
            logger.LogWarning("  [{Status}] {Name}: {Detail}", check.Status, check.Name, check.Detail);
        }

        return report;
    }

    // ---------------------------------------------------------------- audio

    private async Task<List<CheckResult>> AudioChecksAsync(CancellationToken cancellationToken)
    {
        var checks = new List<CheckResult>();

        if (!IsPipeWire)
        {
            checks.Add(new CheckResult("audio-backend", "Audio backend", CheckStatus.Skipped,
                $"Audio backend is '{_panelist.AudioDeviceServiceType}', so there is no PipeWire graph to check."));
            checks.Add(CaptureSignalCheck());
            return checks;
        }

        if (!await PipeWireCli.IsAvailableAsync(cancellationToken))
        {
            checks.Add(new CheckResult("audio-backend", "Audio backend", CheckStatus.Fail,
                "pactl did not respond, so PipeWire is not reachable.",
                "Check that PipeWire and pipewire-pulse are running."));
            return checks;
        }

        checks.Add(new CheckResult("audio-backend", "Audio backend", CheckStatus.Pass, "PipeWire is up."));

        var sources = await ListAsync("sources", cancellationToken);
        var sinks = await ListAsync("sinks", cancellationToken);
        var modules = await ModulesAsync(cancellationToken);

        checks.Add(GraphCheck(sources, sinks));
        checks.Add(OutputPathCheck(sources, modules));
        checks.Add(EchoPathCheck(modules));
        checks.Add(CaptureSignalCheck());

        return checks;
    }

    private CheckResult GraphCheck(HashSet<string> sources, HashSet<string> sinks)
    {
        var missing = new List<string>();

        // Only the configured nodes are required. A run that captures a physical mic
        // instead of a browser tab legitimately has no tab sink.
        if (_pipeWire.DefaultCaptureNode is { Length: > 0 } capture && !sources.Contains(capture))
        {
            missing.Add($"capture source '{capture}'");
        }

        if (_pipeWire.OutputSink is { Length: > 0 } sink && !sinks.Contains(sink))
        {
            missing.Add($"output sink '{sink}'");
        }

        if (missing.Count > 0)
        {
            return new CheckResult("audio-graph", "Audio graph", CheckStatus.Fail,
                $"Missing {string.Join(" and ", missing)}.",
                "Load the audio graph (bubbles-audio.sh up).",
                SetupActions.AudioGraphUp);
        }

        var unset = new List<string>();
        if (_pipeWire.DefaultCaptureNode is null or "") unset.Add("PipeWire:DefaultCaptureNode");
        if (_pipeWire.OutputSink is null or "") unset.Add("PipeWire:OutputSink");

        if (unset.Count > 0)
        {
            // Unset means "use the system default", which is a legitimate choice for a
            // rehearsal and the wrong one on the night - TTS comes out of the laptop
            // speakers instead of going to the stream.
            return new CheckResult("audio-graph", "Audio graph", CheckStatus.Warn,
                $"{string.Join(" and ", unset)} not set, so system defaults are used.",
                "Set them to the nodes bubbles-audio.sh creates.");
        }

        return new CheckResult("audio-graph", "Audio graph", CheckStatus.Pass,
            "The configured capture source and output sink both exist.");
    }

    /// <summary>
    /// Where Bubbles' voice goes, and whether you can hear it.
    ///
    /// Worth stating plainly on every run, because the design makes silence ambiguous:
    /// TTS plays into a null sink whose only consumer is the virtual mic the broadcast
    /// reads. Hearing nothing on this machine is correct, and is also what a completely
    /// dead TTS sounds like.
    /// </summary>
    private CheckResult OutputPathCheck(HashSet<string> sources, string modules)
    {
        var mic = _pipeWire.VirtualMicSource;
        if (mic is null or "")
        {
            return new CheckResult("output-path", "Where Bubbles' voice goes", CheckStatus.Skipped,
                "No virtual mic is configured (PipeWire:VirtualMicSource).");
        }

        if (!sources.Contains(mic))
        {
            return new CheckResult("output-path", "Where Bubbles' voice goes", CheckStatus.Fail,
                $"The virtual mic '{mic}' does not exist, so nothing can pick Bubbles up.",
                "Load the audio graph (bubbles-audio.sh up).",
                SetupActions.AudioGraphUp);
        }

        var captureSink = _pipeWire.DefaultCaptureNode?.Replace(".monitor", string.Empty);

        // A loopback from the mic to anywhere that isn't the capture sink is you listening;
        // one to the capture sink is the rehearsal echo path, which is a different thing.
        var monitoring = modules.Split('\n').Any(line =>
            line.Contains("module-loopback", StringComparison.Ordinal)
            && line.Contains($"source={mic}", StringComparison.Ordinal)
            && (captureSink is null or "" || !line.Contains($"sink={captureSink}", StringComparison.Ordinal)));

        return monitoring
            ? new CheckResult("output-path", "Where Bubbles' voice goes", CheckStatus.Pass,
                $"Into '{mic}' for the stream, and looped to this machine's output so you can hear it.",
                "Turn monitoring off for an in-person event, where the PA already carries it.",
                SetupActions.MonitorOff)
            : new CheckResult("output-path", "Where Bubbles' voice goes", CheckStatus.Pass,
                $"Into '{mic}', which is what the stream reads. You will NOT hear Bubbles on this machine - "
                + "that is correct, not a fault.",
                "To hear it while setting up, turn monitoring on.",
                SetupActions.MonitorOn);
    }

    /// <summary>
    /// The rehearsal echo path loops Bubbles' own voice back into the capture mix. Superb
    /// for rehearsing, ruinous live, and invisible unless something looks for it.
    /// </summary>
    private CheckResult EchoPathCheck(string modules)
    {
        var captureSink = _pipeWire.DefaultCaptureNode?.Replace(".monitor", string.Empty);

        var loopedBack = captureSink is { Length: > 0 }
                         && modules.Split('\n').Any(line =>
                             line.Contains("module-loopback", StringComparison.Ordinal)
                             && line.Contains($"sink={captureSink}", StringComparison.Ordinal)
                             && line.Contains("source=bubbles-mic", StringComparison.Ordinal));

        return loopedBack
            ? new CheckResult("echo-path", "Rehearsal echo path", CheckStatus.Fail,
                "Bubbles' voice is being looped back into the capture mix. This is rehearsal mode.",
                "Turn it off before going live (bubbles-audio.sh echo off).",
                SetupActions.EchoOff)
            : new CheckResult("echo-path", "Rehearsal echo path", CheckStatus.Pass,
                "Off, as it should be for a live run.");
    }

    /// <summary>
    /// The check that would have caught the room microphone in seconds. Reads the live
    /// capture meters rather than opening its own capture, so it cannot disturb a run.
    /// </summary>
    private CheckResult CaptureSignalCheck()
    {
        var selected = deviceService.GetSelectedInputDevices();
        if (selected.Count == 0)
        {
            return new CheckResult("capture-signal", "Capture carrying signal", CheckStatus.Fail,
                "No capture device is selected.",
                "Pick the capture source for this run.");
        }

        var levels = levelMonitor.Snapshot().ToDictionary(l => l.DeviceId);
        if (levels.Count == 0)
        {
            return new CheckResult("capture-signal", "Capture carrying signal", CheckStatus.Warn,
                "Capture is not running, so there is nothing to measure.",
                "Start transcription (enable the panelist), then check again.");
        }

        var silent = new List<string>();
        var stalled = new List<string>();
        var live = new List<string>();

        foreach (var device in selected)
        {
            if (!levels.TryGetValue(device.Id, out var level))
            {
                stalled.Add(device.EffectiveName);
                continue;
            }

            // No blocks at all is a different fault from blocks carrying silence: the
            // capture node itself has stopped.
            if (level.SecondsSinceBlock > 3)
            {
                stalled.Add($"{level.Name} (no audio for {level.SecondsSinceBlock:F0}s)");
            }
            else if (level.SecondsSinceSignal is null or > 20)
            {
                silent.Add(level.Name);
            }
            else
            {
                live.Add($"{level.Name} (peak {level.Peak:F3})");
            }
        }

        if (stalled.Count > 0)
        {
            return new CheckResult("capture-signal", "Capture carrying signal", CheckStatus.Fail,
                $"Capture has stopped on {string.Join(", ", stalled)}.",
                "Check the node still exists and restart capture.");
        }

        if (silent.Count > 0)
        {
            return new CheckResult("capture-signal", "Capture carrying signal", CheckStatus.Warn,
                $"Nothing above the silence threshold on {string.Join(", ", silent)}."
                + (live.Count > 0 ? $" Signal on {string.Join(", ", live)}." : string.Empty),
                "Talk into each mic, or play the panel audio, and watch the meters. Silence here means the transcript gets nothing.");
        }

        return new CheckResult("capture-signal", "Capture carrying signal", CheckStatus.Pass,
            $"Signal on all {selected.Count} selected device(s): {string.Join(", ", live)}.");
    }

    // ---------------------------------------------------------------- models

    private CheckResult CheckWhisperModel()
    {
        if (!string.Equals(_panelist.SttServiceType, "whisper", StringComparison.OrdinalIgnoreCase))
        {
            return new CheckResult("whisper-model", "Whisper model", CheckStatus.Skipped,
                $"Speech to text is '{_panelist.SttServiceType}'.");
        }

        var configured = configuration["Whisper:ModelPath"];
        if (configured is { Length: > 0 })
        {
            return File.Exists(configured)
                ? new CheckResult("whisper-model", "Whisper model", CheckStatus.Pass, $"Present at {configured}.")
                : new CheckResult("whisper-model", "Whisper model", CheckStatus.Fail,
                    $"Whisper:ModelPath points at {configured}, which does not exist.",
                    "Fix the path, or clear it to use the downloaded default.");
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.en.bin");

        return File.Exists(defaultPath)
            ? new CheckResult("whisper-model", "Whisper model", CheckStatus.Pass, "base.en is downloaded.")
            : new CheckResult("whisper-model", "Whisper model", CheckStatus.Warn,
                "Not downloaded yet; it will be fetched on first start, which takes a few minutes.",
                "Start the panelist once before the event, on a connection you trust.");
    }

    /// <summary>
    /// Probes the model with a one-token request. Proves the endpoint, the credentials and
    /// the model name in one go — and loads the model, so the first real answer of the
    /// night isn't the one that pays for it.
    /// </summary>
    private async Task<CheckResult> CheckLanguageModelAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IChatClient>() is not { } chatClient)
        {
            return new CheckResult("llm", "Language model", CheckStatus.Skipped,
                $"LLM service is '{_panelist.LlmServiceType}', which has no probe. "
                + "Set it to 'ChatClient' to have this checked.");
        }

        var model = configuration["ChatClient:Model"] ?? "(from the Aspire connection string)";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            var options = new ChatOptions { MaxOutputTokens = 1, Temperature = 0 };

            // Reasoning off, or a reasoning model spends the whole one-token budget
            // thinking and the probe looks like a failure.
            options.AddOllamaOption(OllamaOption.Think, false);

            var response = await chatClient.GetResponseAsync("Say OK.", options, timeout.Token);

            var tokens = response.Usage?.TotalTokenCount;

            return new CheckResult("llm", "Language model", CheckStatus.Pass,
                $"{model} answered and is now loaded"
                + (tokens is > 0 ? $" ({tokens} tokens)." : "."));
        }
        catch (Exception ex)
        {
            return new CheckResult("llm", "Language model", CheckStatus.Fail,
                $"{model} did not answer: {ex.Message}",
                "Check the inference server is running and the model name is pulled.");
        }
    }

    /// <summary>
    /// Probes whatever summaries run on. Separate from the response probe because they can
    /// point at different endpoints — and when they do, the summary endpoint can be wrong,
    /// unreachable or unauthorised while answers carry on working perfectly. The only
    /// visible symptom is responses quietly losing their sense of the discussion.
    /// </summary>
    private async Task<CheckResult> CheckSummaryModelAsync(CancellationToken cancellationToken)
    {
        var client = services.GetKeyedService<IChatClient>(ChatClientLanguageModelService.SummaryClientKey);
        var responseClient = services.GetService<IChatClient>();

        if (client is null)
        {
            return new CheckResult("summary-model", "Summary model", CheckStatus.Skipped,
                $"LLM service is '{_panelist.LlmServiceType}', which has no separate summary client.");
        }

        var status = summaryHealth.Status;
        var separate = !ReferenceEquals(client, responseClient);

        if (!separate && status.ConsecutiveFailures == 0)
        {
            return new CheckResult("summary-model", "Summary model", CheckStatus.Pass,
                "Same endpoint as responses" + History(status) + ".");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            var options = new ChatOptions { MaxOutputTokens = 1, Temperature = 0 };
            options.AddOllamaOption(OllamaOption.Think, false);

            await client.GetResponseAsync("Say OK.", options, timeout.Token);

            return status.ConsecutiveFailures > 0
                ? new CheckResult("summary-model", "Summary model", CheckStatus.Warn,
                    $"Answering now, but the last {status.ConsecutiveFailures} summary attempt(s) failed: {status.LastError}",
                    "Re-run after the next summary to confirm it has recovered.")
                : new CheckResult("summary-model", "Summary model", CheckStatus.Pass,
                    "Its own endpoint, and it answered" + History(status) + ".");
        }
        catch (Exception ex)
        {
            return new CheckResult("summary-model", "Summary model", CheckStatus.Fail,
                $"The summary endpoint did not answer: {ex.Message}. "
                + "Responses still work, but without any summary of the discussion.",
                "Check ChatClient:Summary - endpoint, model name and API key.");
        }

        static string History(SummaryStatus status) =>
            status switch
            {
                { Successes: 0, ConsecutiveFailures: 0, Abandoned: 0 } => "; no summary has run yet",
                { Successes: 0 } => $"; none of {status.ConsecutiveFailures + status.Abandoned} attempt(s) have produced one",
                _ => $"; {status.Successes} produced so far"
            };
    }

    /// <summary>
    /// Synthesises a short phrase. Proves the TTS server, and for the Qwen path proves the
    /// reference audio is present and loadable, which is otherwise only discovered when
    /// Bubbles is asked to speak.
    /// </summary>
    private async Task<CheckResult> CheckTtsAsync(CancellationToken cancellationToken)
    {
        if (tts is MockTextToSpeechService)
        {
            return new CheckResult("tts", "Text to speech", CheckStatus.Warn,
                "The mock TTS is selected, which produces silence.",
                "Set AIPanelist:TtsServiceType to a real backend before going live.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            var audio = await tts.SynthesizeAsync("Testing, one two.", timeout.Token);

            return audio.Length > 1024
                ? new CheckResult("tts", "Text to speech", CheckStatus.Pass,
                    $"{_panelist.TtsServiceType} returned {audio.Length / 1024}KB of audio, and is now warm.")
                : new CheckResult("tts", "Text to speech", CheckStatus.Fail,
                    $"{_panelist.TtsServiceType} returned only {audio.Length} bytes.",
                    "Check the TTS server's reference audio and its transcript.");
        }
        catch (Exception ex)
        {
            return new CheckResult("tts", "Text to speech", CheckStatus.Fail,
                $"{_panelist.TtsServiceType} failed: {ex.Message}",
                "Check the TTS server is running and its reference audio is present.");
        }
    }

    private CheckResult CheckFallbackLines()
    {
        if (!silenceGuardOptions.Value.Enabled)
        {
            return new CheckResult("fallback-lines", "Fallback lines", CheckStatus.Warn,
                "The silence guard is off, so a response that produces nothing says nothing.",
                "Set SilenceGuard:Enabled to true.");
        }

        var (ready, total, recorded) = fallbackSpeech.Readiness;

        if (recorded > 0)
        {
            return new CheckResult("fallback-lines", "Fallback lines", CheckStatus.Pass,
                $"{recorded} recorded line(s) ready.");
        }

        return ready switch
        {
            0 => new CheckResult("fallback-lines", "Fallback lines", CheckStatus.Warn,
                $"None of the {total} lines are synthesised yet, so a failure would be covered by whatever the TTS manages at the time.",
                "Check the TTS, then re-run: the lines are synthesised in the background at startup."),
            var n when n < total => new CheckResult("fallback-lines", "Fallback lines", CheckStatus.Warn,
                $"{n} of {total} lines are cached.", "Re-run once the TTS is warm."),
            _ => new CheckResult("fallback-lines", "Fallback lines", CheckStatus.Pass,
                $"All {total} lines synthesised and cached.")
        };
    }

    /// <summary>
    /// A display that is not connected shows nothing, and nobody notices until someone
    /// looks at it. Failing rather than warning, because this is a check for going live
    /// and "ready" should mean the audience will see something.
    /// </summary>
    private CheckResult CheckDisplays()
    {
        var connected = displays.ConnectedSeconds;

        if (connected.Count == 0)
        {
            return new CheckResult("displays", "Displays connected", CheckStatus.Fail,
                "No display is connected to the hub.",
                "Switch the display on and check it is pointed at this machine's address.");
        }

        // A connection only seconds old is ordinary just after startup - you have only
        // just switched the display on. Well into a run it means the opposite: a display
        // that keeps dropping and reconnecting.
        var newest = connected[0];
        var settled = displays.Uptime.TotalSeconds > 60;

        if (newest < 5 && settled)
        {
            return new CheckResult("displays", "Displays connected", CheckStatus.Warn,
                $"A display connected only {newest:F0}s ago, though the API has been up for "
                + $"{displays.Uptime.TotalMinutes:F0} minutes. That is a display reconnecting rather than holding.",
                "Watch it for a moment and re-run. If it keeps resetting, check the network between them.");
        }

        var longest = connected[^1];

        return new CheckResult("displays", "Displays connected", CheckStatus.Pass,
            longest >= 60
                ? $"{connected.Count} connected, longest for {longest / 60:F0} minute(s)."
                : $"{connected.Count} connected, longest for {longest:F0}s.");
    }

    /// <summary>
    /// Combinations that are individually valid but together mean a feature silently will
    /// not run. These are logged at startup; nobody reads startup logs on the night.
    /// </summary>
    private IEnumerable<CheckResult> CheckConfiguration()
    {
        var isChatClient = languageModel is ChatClientLanguageModelService;

        if (triggerOptions.Value.Enabled
            && !string.Equals(_panelist.SttServiceType, "whisper", StringComparison.OrdinalIgnoreCase))
        {
            yield return new CheckResult("config-triggers", "Spoken triggers", CheckStatus.Fail,
                "Spoken triggers are on but speech to text is not Whisper, so nothing is listening.",
                "Set AIPanelist:SttServiceType to 'Whisper', or turn spoken triggers off and use the button.");
        }

        if (searchOptions.Value.Enabled && !isChatClient)
        {
            yield return new CheckResult("config-search", "Transcript search", CheckStatus.Fail,
                $"Transcript search is on but {languageModel.GetType().Name} cannot call tools.",
                "Set AIPanelist:LlmServiceType to 'ChatClient'.");
        }

        if (triageOptions.Value.Enabled && (!isChatClient || !streamingOptions.Value.Enabled))
        {
            yield return new CheckResult("config-triage", "Response triage", CheckStatus.Fail,
                "Response triage needs the ChatClient LLM path with streaming enabled.",
                "Set AIPanelist:LlmServiceType to 'ChatClient' and StreamingResponse:Enabled to true.");
        }

        if (triageOptions.Value is { Enabled: true, ThinkingTimeoutSeconds: > 0 } triage
            && silenceGuardOptions.Value is { Enabled: true } guard
            && triage.ThinkingTimeoutSeconds >= guard.MaxSilenceSeconds
            && guard.MaxSilenceSeconds > 0)
        {
            yield return new CheckResult("config-timeouts", "Thinking and silence timeouts", CheckStatus.Warn,
                $"The thinking timeout ({triage.ThinkingTimeoutSeconds}s) is not below the silence guard "
                + $"({guard.MaxSilenceSeconds}s), so a hard question gets a canned quip instead of its answer.",
                "Lower ResponseTriage:ThinkingTimeoutSeconds, or raise SilenceGuard:MaxSilenceSeconds.");
        }
    }

    // ---------------------------------------------------------------- plumbing

    private bool IsPipeWire =>
        string.Equals(_panelist.AudioDeviceServiceType, "pipewire", StringComparison.OrdinalIgnoreCase);

    private static async Task<HashSet<string>> ListAsync(string what, CancellationToken cancellationToken)
    {
        var (_, stdOut, _) = await PipeWireCli.RunAsync("pactl", ["list", "short", what], cancellationToken);

        return stdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length > 1)
            .Select(columns => columns[1].Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<string> ModulesAsync(CancellationToken cancellationToken)
    {
        var (_, stdOut, _) = await PipeWireCli.RunAsync("pactl", ["list", "short", "modules"], cancellationToken);
        return stdOut;
    }

    /// <summary>
    /// Runs a check, times it, and turns an unexpected throw into a failed check rather
    /// than a failed report.
    /// </summary>
    private async Task<CheckResult> TimedAsync(
        string id,
        string name,
        Func<CancellationToken, Task<CheckResult>> check,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await check(cancellationToken);
            return result with { ElapsedMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Readiness check {Id} threw", id);
            return new CheckResult(id, name, CheckStatus.Fail, $"The check itself failed: {ex.Message}",
                ElapsedMs: stopwatch.ElapsedMilliseconds);
        }
    }
}
