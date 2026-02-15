using API.Configuration;
using API.Hubs;
using API.Services;
using API.Services.Implementations;
using API.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSignalR();

// Add controllers
builder.Services.AddControllers();

// Configure AI Panelist options
builder.Services.Configure<AIPanelistOptions>(
    builder.Configuration.GetSection(AIPanelistOptions.SectionName));

// Register transcript buffer service
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AIPanelistOptions>>().Value;
    return new TranscriptBufferService(TimeSpan.FromSeconds(options.TranscriptBufferSeconds));
});

// Configure HttpClient for Ollama
builder.Services.AddHttpClient("Ollama", client =>
{
    var endpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromMinutes(5);
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
        builder.Services.AddSingleton<ILanguageModelService, OllamaLanguageModelService>();
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

// Map controllers
app.MapControllers();

app.Run();
