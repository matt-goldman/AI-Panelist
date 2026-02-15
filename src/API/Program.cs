using API.Configuration;
using API.Hubs;
using API.Services;
using API.Services.Implementations;
using API.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

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

// Register AI services (using mock implementations by default)
builder.Services.AddSingleton<ISpeechToTextService, MockSpeechToTextService>();
builder.Services.AddSingleton<ILanguageModelService, MockLanguageModelService>();
builder.Services.AddSingleton<ITextToSpeechService, MockTextToSpeechService>();
builder.Services.AddSingleton<IAudioDeviceService, MockAudioDeviceService>();
builder.Services.AddSingleton<IAudioPlaybackService, MockAudioPlaybackService>();

// Register orchestrator as both hosted service and singleton
builder.Services.AddSingleton<AIPanelistOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIPanelistOrchestrator>());

var app = builder.Build();

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
