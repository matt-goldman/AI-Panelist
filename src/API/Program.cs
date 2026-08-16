using API.Configuration;
using API.Endpoints;
using API.Hubs;
using API.Services;
using API.Services.Implementations;
using API.Services.Implementations.PipeWire;
using API.Services.Interfaces;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSignalR();

// Configure AI Panelist options
builder.Services.Configure<AIPanelistOptions>(
    builder.Configuration.GetSection(AIPanelistOptions.SectionName));

// Configure prompt configuration
builder.Services.Configure<PromptConfiguration>(
    builder.Configuration.GetSection(PromptConfiguration.SectionName));

// Configure response capture options
builder.Services.Configure<ResponseCaptureOptions>(
    builder.Configuration.GetSection(ResponseCaptureOptions.SectionName));

// Configure PipeWire (Linux audio backend) options
builder.Services.Configure<PipeWireOptions>(
    builder.Configuration.GetSection(PipeWireOptions.SectionName));

// Configure and register the self-suppression gate, which stops Bubbles transcribing
// its own voice when it returns through the StreamYard mix.
builder.Services.Configure<SelfSuppressionOptions>(
    builder.Configuration.GetSection(SelfSuppressionOptions.SectionName));
builder.Services.AddSingleton<SelfSuppressionGate>();

// Guards against Whisper inventing text during the quiet stretches of a panel.
builder.Services.Configure<TranscriptionOptions>(
    builder.Configuration.GetSection(TranscriptionOptions.SectionName));

// Streamed responses: chunk the answer at clause boundaries and speak it as it arrives.
builder.Services.Configure<StreamingResponseOptions>(
    builder.Configuration.GetSection(StreamingResponseOptions.SectionName));
builder.Services.AddSingleton<ResponseChunker>();
builder.Services.AddSingleton<StreamingSpeechPipeline>();

// Register prompt service
builder.Services.AddSingleton<PromptService>();

// Register transcript buffer service
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AIPanelistOptions>>().Value;
    return new TranscriptBufferService(TimeSpan.FromSeconds(options.TranscriptBufferSeconds));
});


// Register AI services based on configuration
var options = builder.Configuration.GetSection(AIPanelistOptions.SectionName).Get<AIPanelistOptions>() 
    ?? new AIPanelistOptions();

// STT Service
switch (options.SttServiceType?.ToLower())
{
    case "whisper":
        builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Information);
        break;
    default:
        builder.Services.AddSingleton<ISpeechToTextService, MockSpeechToTextService>();
        break;
}

// Described in the startup summary once the app is built.
var chatClientDescription = (string?)null;

// LLM Service
//
// The "chatclient" option is the config-driven path: one implementation over
// Microsoft.Extensions.AI's IChatClient, with the provider chosen at runtime by
// ChatClient:Provider. It is the only option that streams, and streaming is what makes
// time-to-first-audio short. The others are kept as-is so nothing that worked stops working.
switch (options.LlmServiceType?.ToLower())
{
    case "chatclient":
        var provider = builder.Configuration["ChatClient:Provider"]?.ToLower() ?? "ollama";

        // Under Aspire the model is already part of the injected connection string, so
        // don't make it be configured twice.
        var aspireOllama = builder.Configuration.GetConnectionString("responses");
        var model = builder.Configuration["ChatClient:Model"]
                    ?? ReadConnectionStringValue(aspireOllama, "Model")
                    ?? throw new InvalidOperationException("ChatClient:Model must be set when LlmServiceType is 'ChatClient'.");

        switch (provider)
        {
            case "ollama":
                // Prefer an explicit endpoint; otherwise use whatever Aspire injected for
                // the "responses" resource, so this works both with a containerised Ollama
                // started by the AppHost and with one you run yourself.
                var ollamaEndpoint = builder.Configuration["ChatClient:Endpoint"]
                                     ?? ResolveAspireOllamaEndpoint(aspireOllama)
                                     ?? "http://localhost:11434";

                chatClientDescription = $"Ollama {model} at {ollamaEndpoint}";
                builder.Services.AddSingleton<IChatClient>(
                    new OllamaSharp.OllamaApiClient(new Uri(ollamaEndpoint), model));
                break;

            case "openai":
                // Also covers any OpenAI-compatible endpoint (a local vLLM, a gateway, and
                // so on) via ChatClient:Endpoint.
                var apiKey = builder.Configuration["ChatClient:ApiKey"]
                             ?? throw new InvalidOperationException("ChatClient:ApiKey must be set for the OpenAI provider. Use user-secrets, not appsettings.");
                var openAiOptions = new OpenAI.OpenAIClientOptions();
                if (builder.Configuration["ChatClient:Endpoint"] is { Length: > 0 } customEndpoint)
                {
                    openAiOptions.Endpoint = new Uri(customEndpoint);
                }

                chatClientDescription = $"OpenAI {model} at {openAiOptions.Endpoint?.ToString() ?? "api.openai.com"}";
                builder.Services.AddSingleton<IChatClient>(
                    new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), openAiOptions)
                        .GetChatClient(model)
                        .AsIChatClient());
                break;

            case "azureaiinference":
                var azureEndpoint = builder.Configuration["ChatClient:Endpoint"]
                                    ?? throw new InvalidOperationException("ChatClient:Endpoint must be set for the Azure AI Inference provider.");
                var azureKey = builder.Configuration["ChatClient:ApiKey"]
                               ?? throw new InvalidOperationException("ChatClient:ApiKey must be set for the Azure AI Inference provider.");

                chatClientDescription = $"Azure AI Inference {model} at {azureEndpoint}";
                builder.Services.AddSingleton<IChatClient>(
                    new Azure.AI.Inference.ChatCompletionsClient(
                            new Uri(azureEndpoint), new Azure.AzureKeyCredential(azureKey))
                        .AsIChatClient(model));
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown ChatClient:Provider '{provider}'. Expected 'Ollama', 'OpenAI' or 'AzureAIInference'.");
        }

        builder.Services.AddSingleton<ILanguageModelService, ChatClientLanguageModelService>();
        break;

    case "ollama":
        // Configure HttpClient for Ollama
        builder.Services.AddHttpClient("Ollama", client =>
        {
            var endpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        // Use factory to explicitly call HttpClient constructor
        builder.Services.AddSingleton<ILanguageModelService>(sp =>
            new OllamaLanguageModelService(
                sp.GetRequiredService<ILogger<OllamaLanguageModelService>>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<PromptService>()));
        break;
    case "ollamaaspire":
        builder.AddOllamaApiClient("responses").AddChatClient();
        // Use factory to explicitly call IChatClient constructor
        builder.Services.AddSingleton<ILanguageModelService>(sp =>
            new OllamaLanguageModelService(
                sp.GetRequiredService<ILogger<OllamaLanguageModelService>>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<PromptService>()));
        break;
    case "foundrylocal":
        // Add Azure AI Foundry client

        // NOTE: this doesn't work:
        builder.AddAzureChatCompletionsClient("responses");
        // It simply can't find the model. Doesn't work with multiple variations both here and in AppHost

        // NOTE: This _might_ work, but requirees refactoring FoundryLocalLanguageModelService to use IChatClient instead:
        // builder.AddFoundryLocalAIServices();
        // but ultimately I stopped trying to make this work, because Aspire won't used the cached model anyway. So too many papercuts.

        builder.Services.AddSingleton<ILanguageModelService, FoundryLocalLanguageModelService>();
        break;
    default:
        builder.Services.AddSingleton<ILanguageModelService, MockLanguageModelService>();
        break;
}

// TTS Service
switch (options.TtsServiceType?.ToLower())
{
    case "azure":
        builder.Services.AddSingleton<ITextToSpeechService, AzureTextToSpeechService>();
        break;
    case "espeak":
        // Rehearsal/fallback: real words, no GPU, no model download.
        builder.Services.AddSingleton<ITextToSpeechService, EspeakTextToSpeechService>();
        break;
    case "qwen3-tts":
        // Configure HttpClient for Qwen TTS
        builder.Services.AddHttpClient<ITextToSpeechService, QwenTextToSpeechService>(client =>
        {
            var endpoint = builder.Configuration["QwenTts:Endpoint"] ?? "http://localhost:8000";
            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        break;
    default:
        builder.Services.AddSingleton<ITextToSpeechService, MockTextToSpeechService>();
        break;
}

// Audio Device Service
switch (options.AudioDeviceServiceType?.ToLower())
{
    case "windows":
        builder.Services.AddSingleton<IAudioDeviceService, WindowsAudioDeviceService>();
        break;
    case "pipewire":
        builder.Services.AddSingleton<IAudioDeviceService, PipeWireAudioDeviceService>();
        break;
    default:
        builder.Services.AddSingleton<IAudioDeviceService, MockAudioDeviceService>();
        break;
}

// Audio Capture Factory - the seam that keeps Whisper free of any platform audio API.
// Follows the audio device backend, since capture targets are identified by that backend's IDs.
switch (options.AudioDeviceServiceType?.ToLower())
{
    case "windows":
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "AIPanelist:AudioDeviceServiceType is 'Windows' but this is not Windows. Use 'PipeWire' on Linux.");
        }
        builder.Services.AddSingleton<IAudioCaptureFactory, NAudioAudioCaptureFactory>();
        break;
    case "pipewire":
        builder.Services.AddSingleton<IAudioCaptureFactory, PipeWireAudioCaptureFactory>();
        break;
    default:
        // Mock devices produce no audio; Whisper would sit idle, so fail loudly instead
        // only if it is actually asked for. Registering the PipeWire factory keeps DI valid.
        builder.Services.AddSingleton<IAudioCaptureFactory, PipeWireAudioCaptureFactory>();
        break;
}

// Audio Playback Service
switch (options.AudioPlaybackServiceType?.ToLower())
{
    case "windows":
        builder.Services.AddSingleton<IAudioPlaybackService, WindowsAudioPlaybackService>();
        break;
    case "pipewire":
        builder.Services.AddSingleton<IAudioPlaybackService, PipeWireAudioPlaybackService>();
        break;
    default:
        builder.Services.AddSingleton<IAudioPlaybackService, MockAudioPlaybackService>();
        break;
}

// Register orchestrator as both hosted service and singleton
builder.Services.AddSingleton<AIPanelistOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIPanelistOrchestrator>());

// Register response capture service (for capturing LLM responses and TTS audio)
builder.Services.AddSingleton<ResponseCaptureService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResponseCaptureService>());

// Enumerate filler phrase files from wwwroot/audio/filler-phrases
builder.Services.PostConfigure<AIPanelistOptions>(opts =>
{
    var env = builder.Environment;
    var fillerPhrasesPath = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "audio", "filler-phrases");

    if (Directory.Exists(fillerPhrasesPath))
    {
        var audioExtensions = new[] { ".wav", ".mp3", ".ogg" };
        var files = Directory.GetFiles(fillerPhrasesPath)
            .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        opts.FillerPhraseFiles = files;
    }

    var audioRootPath = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "audio");
    var introFile = Path.Combine(audioRootPath, "Intro.wav");
    if (File.Exists(introFile))
    {
        opts.IntroPhrase = introFile;
    }
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Log which services are being used
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("AI Panelist Service Configuration:");
logger.LogInformation("  STT: {Service}", options.SttServiceType ?? "Mock");
logger.LogInformation("  LLM: {Service}", options.LlmServiceType ?? "Mock");
logger.LogInformation("  TTS: {Service}", options.TtsServiceType ?? "Mock");
logger.LogInformation("  Audio Device: {Service}", options.AudioDeviceServiceType ?? "Mock");
logger.LogInformation("  Audio Playback: {Service}", options.AudioPlaybackServiceType ?? "Mock");

// Log streaming configuration - if the selected LLM can't stream, time-to-first-audio
// silently falls back to "wait for the whole answer", which is worth saying out loud.
var streamingOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StreamingResponseOptions>>().Value;
var languageModel = app.Services.GetRequiredService<ILanguageModelService>();
if (chatClientDescription is not null) logger.LogInformation("    Chat client: {Description}", chatClientDescription);
logger.LogInformation("  Streaming responses: {Status}", streamingOptions.Enabled ? "Enabled" : "Disabled (filler phrases active)");
if (streamingOptions.Enabled && !languageModel.SupportsStreaming)
{
    logger.LogWarning(
        "    {Service} does not stream - the whole response will be generated before any audio plays. "
        + "Set AIPanelist:LlmServiceType to 'ChatClient' for real streaming.",
        languageModel.GetType().Name);
}

// Log response capture configuration
var captureOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ResponseCaptureOptions>>().Value;
logger.LogInformation("  Response Capture: {Status}", captureOptions.Enabled ? "Enabled" : "Disabled");
if (captureOptions.Enabled)
{
    logger.LogInformation("    Text: {Status} -> {Dir}", captureOptions.CaptureTextResponses ? "Yes" : "No", captureOptions.TextOutputDirectory);
    logger.LogInformation("    Audio: {Status} -> {Dir}", captureOptions.CaptureAudioResponses ? "Yes" : "No", captureOptions.AudioOutputDirectory);
}

// Log filler phrases
var fillerOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AIPanelistOptions>>().Value;
logger.LogInformation("  Filler Phrases: {Count} files found", fillerOptions.FillerPhraseFiles.Count);
foreach (var file in fillerOptions.FillerPhraseFiles)
{
    logger.LogDebug("    - {File}", Path.GetFileName(file));
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // disable forced HTTPS for dev so mobile apps can conenct without certificate issues
    app.UseHttpsRedirection();
}

app.MapHub<BubblesHub>("/bubbles");

// Map minimal API endpoints
app.MapPanelistEndpoints();
app.MapAudioDevicesEndpoints();

app.Run();

/// <summary>
/// Pull a base URL out of the connection string Aspire injects for the Ollama resource.
/// It arrives either as a bare URL or as "Endpoint=http://...;Model=...", depending on
/// which hosting integration produced it.
/// </summary>
static string? ResolveAspireOllamaEndpoint(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return null;

    return Uri.TryCreate(connectionString, UriKind.Absolute, out var direct)
        ? direct.ToString()
        : ReadConnectionStringValue(connectionString, "Endpoint");
}

/// <summary>
/// Read one key out of a "Key=value;Key=value" connection string.
/// </summary>
static string? ReadConnectionStringValue(string? connectionString, string key)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return null;

    foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = part.IndexOf('=');
        if (separator <= 0) continue;

        if (part[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            var value = part[(separator + 1)..].Trim();
            if (value.Length > 0) return value;
        }
    }

    return null;
}
