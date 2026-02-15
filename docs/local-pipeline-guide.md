# AI Panelist Local Pipeline - Developer Guide

## Overview

This implementation provides a fully functional local AI panelist pipeline that:

1. **Continuously captures and transcribes audio** from a microphone
2. **Maintains a rolling transcript buffer** (~2-3 minutes)
3. **Periodically generates summaries** of the conversation (every 30-60 seconds)
4. **Generates and speaks responses** when triggered by the moderator
5. **Manages panelist state** (Idle, Listening, Thinking, Speaking) via SignalR
6. **Supports cancellation and disabling** of the AI panelist

## Architecture

### Core Components

```
AIPanelistOrchestrator (Coordinator)
├── ISpeechToTextService (Transcription)
├── ILanguageModelService (Summary & Response Generation)
├── ITextToSpeechService (Speech Synthesis)
├── IAudioPlaybackService (Audio Output)
├── IAudioDeviceService (Device Management)
└── TranscriptBufferService (Rolling Buffer)
```

### Service Interfaces

All AI services are abstracted behind interfaces in `Services/Interfaces/`:

- **ISpeechToTextService** - Continuous audio transcription
- **ILanguageModelService** - Summary and response generation
- **ITextToSpeechService** - Text-to-speech synthesis
- **IAudioPlaybackService** - Audio playback
- **IAudioDeviceService** - Audio device enumeration and selection

### Mock Implementations

Mock implementations are provided in `Services/Implementations/` for testing without external dependencies:

- `MockSpeechToTextService` - Generates periodic mock transcriptions
- `MockLanguageModelService` - Returns placeholder summaries and responses
- `MockTextToSpeechService` - Simulates TTS processing time
- `MockAudioPlaybackService` - Simulates audio playback
- `MockAudioDeviceService` - Returns mock device list

## Configuration

Configuration is in `appsettings.json` under the `AIPanelist` section:

```json
{
  "AIPanelist": {
    "AudioInputDeviceId": null,           // null = default device
    "TranscriptBufferSeconds": 180,        // 3 minutes
    "SummaryIntervalSeconds": 45,          // Generate summary every 45s
    "MaxResponseWords": 150,               // Max words in AI response
    "EnableFillerPhrases": true,           // Play filler before response
    "FillerPhraseFiles": [],               // Paths to filler audio files
    "SttServiceType": "Mock",              // STT implementation
    "LlmServiceType": "Mock",              // LLM implementation
    "TtsServiceType": "Mock"               // TTS implementation
  }
}
```

## API Endpoints

### Panelist Control

- `POST /api/panelist/trigger` - Trigger AI response generation
- `POST /api/panelist/cancel` - Cancel current response
- `POST /api/panelist/disable` - Disable the AI panelist
- `POST /api/panelist/enable` - Re-enable the AI panelist

### Device Management

- `GET /api/panelist/devices` - List available audio input devices
- `GET /api/panelist/devices/selected` - Get currently selected device
- `POST /api/panelist/devices/select/{deviceId}` - Select an audio device

## SignalR Integration

The moderator app can trigger responses by setting the panelist state to `Listening`:

```csharp
await hubConnection.SendAsync("UpdatePanelState", AiPanelistState.Listening);
```

The orchestrator will automatically:
1. Set state to `Thinking`
2. Generate a response
3. Set state to `Speaking`
4. Play the response
5. Return to `Listening`

## State Transitions

```
Idle ──────────────────────────────────────┐
  │                                         │
  └─> Listening ──> Thinking ──> Speaking ──┘
           │            │           │
           └────────────┴───────────┴─> (on cancel/disable)
```

During response generation:
- **Thinking**: Pauses STT, generates response
- **Speaking**: Plays TTS audio, STT remains paused
- Returns to **Listening**: Resumes STT

## Swapping Implementations

### 1. Implement the Service Interface

Create a new class implementing one of the service interfaces:

```csharp
public class WhisperSpeechToTextService : ISpeechToTextService
{
    // Implement interface methods
    public async Task StartTranscriptionAsync(CancellationToken cancellationToken)
    {
        // Start Whisper transcription
    }
    
    // ... other methods
}
```

### 2. Register in Program.cs

Replace the mock registration with your implementation:

```csharp
// Replace this:
builder.Services.AddSingleton<ISpeechToTextService, MockSpeechToTextService>();

// With this:
builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
```

### 3. Add Dependencies

Add any required NuGet packages to `API.csproj`:

```xml
<PackageReference Include="Whisper.net" Version="..." />
```

## Example: Whisper STT Integration

```csharp
public class WhisperSpeechToTextService : ISpeechToTextService
{
    private readonly ILogger<WhisperSpeechToTextService> _logger;
    private readonly IAudioDeviceService _audioDeviceService;
    private CancellationTokenSource? _cts;
    private bool _isPaused;

    public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

    public async Task StartTranscriptionAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Initialize Whisper model
        using var processor = await WhisperFactory.CreateProcessorAsync();
        
        // Get audio device
        var device = _audioDeviceService.GetSelectedInputDevice();
        
        // Start audio capture and transcription loop
        await CaptureAndTranscribeAsync(processor, device, _cts.Token);
    }

    private async Task CaptureAndTranscribeAsync(...)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (!_isPaused)
            {
                // Capture audio chunk
                var audioData = await CaptureAudioChunkAsync();
                
                // Transcribe
                var result = await processor.ProcessAsync(audioData);
                
                // Raise event
                TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                {
                    Text = result.Text,
                    Timestamp = DateTime.UtcNow,
                    IsFinal = true
                });
            }
        }
    }

    // Implement other interface methods...
}
```

## Example: Ollama LLM Integration

```csharp
public class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaLanguageModelService> _logger;
    private const string OllamaEndpoint = "http://localhost:11434/api/generate";

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken)
    {
        var prompt = $@"Summarise the following transcript into 5-8 concise bullet points.
Focus on key themes, points of disagreement, strong claims, and open questions.

Transcript:
{transcript}

Summary (bullet points):";

        var response = await _httpClient.PostAsJsonAsync(OllamaEndpoint, new
        {
            model = "llama2",
            prompt = prompt,
            stream = false
        }, cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
        return result?.Response ?? string.Empty;
    }

    public async Task<string> GenerateResponseAsync(string summary, string recentTranscript, CancellationToken cancellationToken)
    {
        var prompt = $@"You are a moderated AI panelist. Generate a thoughtful, conversational response (≤150 words).

Current summary:
{summary}

Recent discussion:
{recentTranscript}

Your response:";

        // Similar implementation...
    }
}
```

## Example: System TTS Integration

```csharp
public class SystemTextToSpeechService : ITextToSpeechService
{
    private readonly SpeechSynthesizer _synthesizer;
    private CancellationTokenSource? _cts;

    public bool IsSpeaking { get; private set; }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            // Use system TTS or external service
            await _synthesizer.SpeakTextAsync(text);
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    public Task StopAsync()
    {
        _synthesizer.SpeakAsyncCancelAll();
        _cts?.Cancel();
        return Task.CompletedTask;
    }
}
```

## Audio Device Selection

Audio devices can be configured at startup via `appsettings.json` or selected at runtime:

```bash
# List available devices
curl http://localhost:5141/api/panelist/devices

# Select a device
curl -X POST http://localhost:5141/api/panelist/devices/select/device-id-here
```

## Filler Phrases

To add filler phrases:

1. Generate or record short audio files (1-2 seconds)
2. Place them in a known location (e.g., `Resources/FillerPhrases/`)
3. Add paths to `appsettings.json`:

```json
{
  "AIPanelist": {
    "FillerPhraseFiles": [
      "./Resources/FillerPhrases/umm.wav",
      "./Resources/FillerPhrases/let-me-think.wav",
      "./Resources/FillerPhrases/interesting.wav"
    ]
  }
}
```

The system will randomly select and play one filler phrase while generating the response.

## Cancellation and Error Handling

All long-running operations support cancellation via `CancellationToken`:

- **Transcription**: Can be stopped via `StopTranscriptionAsync()`
- **Response Generation**: Cancelled via `CancelResponseAsync()`
- **TTS**: Stopped via `StopAsync()`

The orchestrator handles errors gracefully and logs them without crashing.

## Testing

### Manual Testing

1. Start the API:
   ```bash
   cd src/API
   dotnet run
   ```

2. Trigger a response:
   ```bash
   curl -X POST http://localhost:5141/api/panelist/trigger
   ```

3. Watch the logs to see state transitions

### Integration Testing

Connect the Moderator and Bubbles MAUI apps to test the full SignalR integration:

1. Configure the API URL in both apps
2. Use the Moderator app to trigger responses
3. Watch the Bubbles app animate state changes

## Deployment Considerations

### Running on a Single Machine

The entire system (API + Inference Runtimes) runs on a single machine:

- **API**: Coordinates everything
- **STT Runtime**: Local process (e.g., Whisper)
- **LLM Runtime**: Local process (e.g., Ollama, LocalAI)
- **TTS Runtime**: Local service or system TTS

### Resource Requirements

- **GPU**: Recommended for Whisper STT and local LLM inference
- **RAM**: 8GB minimum, 16GB+ recommended for larger models
- **CPU**: Multi-core processor for concurrent operations

### Audio Setup

- **Microphone**: Connect to the host machine
- **Speaker**: Audio plays from host (not MAUI app)
- Place wireless speaker near the "Bubbles" display for physical presence

## Troubleshooting

### STT Not Working

- Check audio device selection
- Verify microphone permissions
- Test with mock implementation first

### LLM Responses Too Slow

- Use smaller, faster models
- Enable GPU acceleration
- Reduce context window

### State Transitions Not Broadcasting

- Verify SignalR connection
- Check hub URL configuration
- Review API logs for errors

### Audio Playback Issues

- Test audio device output
- Verify audio file formats
- Check playback service logs

## Security Considerations

- **No Authentication**: Add authentication for production use
- **Local Only**: System designed for local, trusted environment
- **No Persistence**: Transcripts not saved (add if needed)
- **Resource Limits**: Monitor CPU/GPU/memory usage

## Next Steps

1. **Swap Mock STT** with Whisper or similar
2. **Swap Mock LLM** with Ollama or LocalAI
3. **Swap Mock TTS** with system TTS or Piper
4. **Add Real Audio Capture** using NAudio or similar
5. **Test with Real Hardware** and microphone setup
6. **Generate Filler Phrases** and add to configuration
7. **Tune Response Generation** with custom prompts
8. **Add Logging** for post-event analysis

## License

See LICENSE file in repository root.
