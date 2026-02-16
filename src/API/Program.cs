using API.Configuration;
using API.Endpoints;
using API.Hubs;
using API.Services;
using API.Services.Implementations;
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

// LLM Service
switch (options.LlmServiceType?.ToLower())
{
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
    case "qwen3-tts":
        builder.Services.AddSingleton<ITextToSpeechService, QwenTextToSpeechService>();
        // Configure HttpClient for Qwen TTS
        builder.Services.AddHttpClient<QwenTextToSpeechService>(client =>
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
    default:
        builder.Services.AddSingleton<IAudioDeviceService, MockAudioDeviceService>();
        break;
}

// Audio Playback Service
switch (options.AudioPlaybackServiceType?.ToLower())
{
    case "windows":
        builder.Services.AddSingleton<IAudioPlaybackService, WindowsAudioPlaybackService>();
        break;
    default:
        builder.Services.AddSingleton<IAudioPlaybackService, MockAudioPlaybackService>();
        break;
}

// Register orchestrator as both hosted service and singleton
builder.Services.AddSingleton<AIPanelistOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIPanelistOrchestrator>());

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
