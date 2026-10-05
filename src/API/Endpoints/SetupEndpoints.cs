using API.Configuration;
using API.Services;
using API.Services.Setup;
using Microsoft.Extensions.Options;

namespace API.Endpoints;

/// <summary>
/// What the setup app at /setup talks to.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup");

        group.MapGet("/readiness", RunReadinessAsync)
            .WithName("GetReadiness")
            .WithSummary("Run every pre-flight check and report what is and is not ready");

        group.MapGet("/levels", GetLevels)
            .WithName("GetCaptureLevels")
            .WithSummary("Live input level per capture device, for checking which mic is which");

        group.MapGet("/actions", GetActions)
            .WithName("GetSetupActions")
            .WithSummary("Fixes the setup app can apply itself");

        group.MapPost("/actions/{id}", RunActionAsync)
            .WithName("RunSetupAction")
            .WithSummary("Apply one of the fixes listed by /api/setup/actions");

        group.MapPost("/test-speech", TestSpeechAsync)
            .WithName("TestSpeech")
            .WithSummary("Speak a line through the whole audio path, so the routing and the display's mouth can be seen working");

        group.MapGet("/summary", GetSummary)
            .WithName("GetSetupSummary")
            .WithSummary("What this run is configured to do, as the setup app displays it");

        return app;
    }

    private static async Task<IResult> RunReadinessAsync(
        ReadinessService readiness,
        CancellationToken cancellationToken)
        => Results.Ok(await readiness.RunAsync(cancellationToken));

    private static IResult GetLevels(CaptureLevelMonitor monitor, DisplayRegistry displays)
        => Results.Ok(new
        {
            silenceThreshold = monitor.SilenceThreshold,
            devices = monitor.Snapshot(),
            displays = displays.Count
        });

    /// <summary>
    /// The one check that can only be made by a human: play a line and see whether it comes
    /// out of the right place with the mouth moving.
    /// </summary>
    private static async Task<IResult> TestSpeechAsync(
        TestSpeechRequest? request,
        AIPanelistOrchestrator orchestrator,
        DisplayRegistry displays,
        CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(request?.Text) ? TestSpeechRequest.Default : request!.Text!;
        var failure = await orchestrator.SpeakTestLineAsync(text, cancellationToken);

        return failure is null
            ? Results.Ok(new
            {
                message = displays.Count > 0
                    ? $"Spoken. Watch the {displays.Count} connected display(s): the mouth should have moved with it."
                    : "Spoken, but no display is connected to watch it.",
                text,
                displays = displays.Count
            })
            : Results.BadRequest(new { message = failure, text });
    }

    private static IResult GetActions(IOptions<SetupOptions> options)
        => Results.Ok(new
        {
            enabled = options.Value.AllowAudioGraphActions,
            actions = SetupActions.Available.Select(a => new { id = a.Id, describes = a.Describes })
        });

    private static async Task<IResult> RunActionAsync(
        string id,
        SetupActions actions,
        CancellationToken cancellationToken)
    {
        var result = await actions.RunAsync(id, cancellationToken);
        return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
    }

    /// <summary>
    /// The settings that differ between runs, so the operator can see at a glance what
    /// this one is set up to do without reading appsettings on a laptop on the floor.
    /// </summary>
    private static IResult GetSummary(
        IOptions<AIPanelistOptions> panelist,
        IOptions<PipeWireOptions> pipeWire,
        IOptions<StreamingResponseOptions> streaming,
        IOptions<TriggerPhraseOptions> triggers,
        IOptions<TranscriptSearchOptions> search,
        IOptions<ResponseTriageOptions> triage,
        IOptions<SilenceGuardOptions> silenceGuard,
        IOptions<SpeechEnvelopeOptions> envelope,
        TriggerPhraseMatcher matcher,
        IConfiguration configuration)
        => Results.Ok(new
        {
            services = new
            {
                stt = panelist.Value.SttServiceType,
                llm = panelist.Value.LlmServiceType,
                tts = panelist.Value.TtsServiceType,
                audioDevice = panelist.Value.AudioDeviceServiceType,
                audioPlayback = panelist.Value.AudioPlaybackServiceType
            },
            model = configuration["ChatClient:Model"],
            endpoint = configuration["ChatClient:Endpoint"],
            audio = new
            {
                captureNode = pipeWire.Value.DefaultCaptureNode,
                outputSink = pipeWire.Value.OutputSink
            },
            features = new
            {
                streaming = streaming.Value.Enabled,
                spokenTriggers = triggers.Value.Enabled,
                transcriptSearch = search.Value.Enabled,
                responseTriage = triage.Value.Enabled,
                silenceGuard = silenceGuard.Value.Enabled,
                speechEnvelope = envelope.Value.Enabled
            },
            triggerPhrases = triggers.Value.Enabled ? matcher.Phrases : []
        });
}

/// <summary>
/// The line to speak for the end-to-end audio and display test.
/// </summary>
public sealed class TestSpeechRequest
{
    /// <summary>
    /// Says out loud what it is proving, so whoever is standing by the speakers knows what
    /// they are listening for.
    /// </summary>
    public const string Default =
        "This is Bubbles, testing. If you can hear me, and my mouth is moving, the whole path is working.";

    public string? Text { get; set; }
}
