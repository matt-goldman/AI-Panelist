# Example Real Implementations

This document provides example code for replacing mock implementations with real services.

## Example 1: NAudio + Whisper.net STT Service

This example shows how to implement real audio capture and transcription using NAudio for audio capture and Whisper.net for transcription.

### Required NuGet Packages

```xml
<PackageReference Include="NAudio" Version="2.2.1" />
<PackageReference Include="Whisper.net" Version="1.4.7" />
<PackageReference Include="Whisper.net.Runtime" Version="1.4.7" />
```

### Implementation

```csharp
using API.Services.Interfaces;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace API.Services.Implementations;

public class WhisperSpeechToTextService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WhisperSpeechToTextService> _logger;
    private readonly IAudioDeviceService _audioDeviceService;
    
    private WaveInEvent? _waveIn;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private bool _isPaused;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();

    public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

    public WhisperSpeechToTextService(
        ILogger<WhisperSpeechToTextService> logger,
        IAudioDeviceService audioDeviceService)
    {
        _logger = logger;
        _audioDeviceService = audioDeviceService;
    }

    public async Task StartTranscriptionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Whisper transcription");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Download Whisper model if needed
        var modelPath = await DownloadWhisperModelAsync();

        // Initialize Whisper processor
        using var factory = WhisperFactory.FromPath(modelPath);
        _processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt("This is a technology panel discussion.")
            .Build();

        // Start audio capture
        StartAudioCapture();

        // Start transcription loop
        _ = Task.Run(() => TranscriptionLoopAsync(_cts.Token), _cts.Token);
    }

    public Task StopTranscriptionAsync()
    {
        _logger.LogInformation("Stopping Whisper transcription");
        _cts?.Cancel();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        return Task.CompletedTask;
    }

    public Task PauseTranscriptionAsync()
    {
        _logger.LogInformation("Pausing Whisper transcription");
        _isPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeTranscriptionAsync()
    {
        _logger.LogInformation("Resuming Whisper transcription");
        _isPaused = false;
        return Task.CompletedTask;
    }

    private void StartAudioCapture()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit, mono
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.RecordingStopped += (s, e) => _logger.LogInformation("Audio recording stopped");
        _waveIn.StartRecording();

        _logger.LogInformation("Audio capture started");
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_isPaused) return;

        // Convert byte array to float array (16-bit PCM to float)
        var floatBuffer = new float[e.BytesRecorded / 2];
        for (int i = 0; i < floatBuffer.Length; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            floatBuffer[i] = sample / 32768f; // Normalize to [-1, 1]
        }

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(floatBuffer);
        }
    }

    private async Task TranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        const int samplesPerSegment = 16000 * 10; // 10 seconds of audio

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5000, cancellationToken); // Process every 5 seconds

            if (_isPaused) continue;

            float[] audioSegment;
            lock (_bufferLock)
            {
                if (_audioBuffer.Count < samplesPerSegment / 2) continue; // Wait for more data

                audioSegment = _audioBuffer.Take(samplesPerSegment).ToArray();
                _audioBuffer.RemoveRange(0, Math.Min(samplesPerSegment, _audioBuffer.Count));
            }

            try
            {
                await foreach (var segment in _processor!.ProcessAsync(audioSegment, cancellationToken))
                {
                    var text = segment.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogDebug("Transcription: {Text}", text);
                        
                        TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                        {
                            Text = text,
                            Timestamp = DateTime.UtcNow,
                            IsFinal = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during transcription");
            }
        }
    }

    private async Task<string> DownloadWhisperModelAsync()
    {
        var modelDir = Path.Combine(AppContext.BaseDirectory, "Models");
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, "ggml-base.en.bin");
        
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Downloading Whisper model...");
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.BaseEn);
            using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream);
            _logger.LogInformation("Whisper model downloaded to {Path}", modelPath);
        }

        return modelPath;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _waveIn?.Dispose();
        _processor?.Dispose();
    }
}
```

### Registration in Program.cs

```csharp
builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
```

---

## Example 2: Ollama LLM Service

This example uses Ollama's REST API for local LLM inference.

### Implementation

```csharp
using API.Services.Interfaces;
using System.Text.Json;

namespace API.Services.Implementations;

public class OllamaLanguageModelService : ILanguageModelService
{
    private readonly ILogger<OllamaLanguageModelService> _logger;
    private readonly HttpClient _httpClient;
    private const string OllamaEndpoint = "http://localhost:11434/api/generate";

    public OllamaLanguageModelService(
        ILogger<OllamaLanguageModelService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Ollama");
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating summary via Ollama");

        var prompt = $@"Summarise the following transcript into 5-8 concise bullet points.
Focus on key themes, points of disagreement, strong claims, and open questions.
Avoid repetition and speculation.

Transcript:
{transcript}

Summary (bullet points only, no introduction):";

        var response = await GenerateAsync("llama2", prompt, cancellationToken);
        
        _logger.LogDebug("Generated summary: {Summary}", response);
        return response;
    }

    public async Task<string> GenerateResponseAsync(
        string summary, 
        string recentTranscript, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating response via Ollama");

        var systemPrompt = @"You are a moderated AI panelist participating in a live technology discussion.

Constraints:
- You are not sentient and do not have emotions
- You do not attack individuals or make moral accusations
- Use light, self-deprecating humour only
- Keep responses under 150 words
- Speak conversationally
- If context is unclear, briefly acknowledge and respond anyway

Your goal: Be thoughtful, measured, occasionally witty, and respectful.";

        var prompt = $@"{systemPrompt}

Current discussion summary:
{summary}

Recent transcript excerpt:
{recentTranscript}

Generate a conversational response (≤150 words):";

        var response = await GenerateAsync("llama2", prompt, cancellationToken);
        
        _logger.LogDebug("Generated response: {Response}", response);
        return response;
    }

    private async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = model,
            prompt = prompt,
            stream = false,
            options = new
            {
                temperature = 0.7,
                top_p = 0.9,
                max_tokens = 300
            }
        };

        try
        {
            var jsonRequest = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(OllamaEndpoint, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<OllamaResponse>(jsonResponse);

            return result?.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama API");
            throw;
        }
    }

    private class OllamaResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}
```

### Configuration in Program.cs

```csharp
builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri("http://localhost:11434");
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddSingleton<ILanguageModelService, OllamaLanguageModelService>();
```

### Prerequisites

1. Install Ollama: https://ollama.ai/download
2. Pull a model: `ollama pull llama2`
3. Ensure Ollama is running: `ollama serve`

---

## Example 3: Azure Cognitive Services TTS

This example uses Azure Cognitive Services for high-quality text-to-speech.

### Required NuGet Packages

Already included:
```xml
<PackageReference Include="Microsoft.CognitiveServices.Speech" Version="1.48.1" />
```

### Implementation

```csharp
using API.Services.Interfaces;
using Microsoft.CognitiveServices.Speech;

namespace API.Services.Implementations;

public class AzureTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<AzureTextToSpeechService> _logger;
    private readonly SpeechConfig _speechConfig;
    private SpeechSynthesizer? _synthesizer;
    private CancellationTokenSource? _cts;

    public bool IsSpeaking { get; private set; }

    public AzureTextToSpeechService(
        ILogger<AzureTextToSpeechService> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        var subscriptionKey = configuration["AzureVoiceResourceKey"];
        var region = configuration["AzureVoiceRegion"];

        if (string.IsNullOrEmpty(subscriptionKey) || string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException("Azure Speech credentials not configured");
        }

        _speechConfig = SpeechConfig.FromSubscription(subscriptionKey, region);
        _speechConfig.SpeechSynthesisVoiceName = "en-AU-WilliamNeural"; // Australian male voice
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure TTS: Speaking {Length} characters", text.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            using var synthesizer = new SpeechSynthesizer(_speechConfig);
            
            var result = await synthesizer.SpeakTextAsync(text);

            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            {
                _logger.LogInformation("Azure TTS: Speech completed successfully");
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                _logger.LogError("Azure TTS: Cancelled - {Reason}", cancellation.Reason);
                
                if (cancellation.Reason == CancellationReason.Error)
                {
                    _logger.LogError("Azure TTS: Error - {ErrorCode}: {ErrorDetails}", 
                        cancellation.ErrorCode, cancellation.ErrorDetails);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Azure TTS: Speech cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure TTS: Error during speech synthesis");
            throw;
        }
        finally
        {
            IsSpeaking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Azure TTS: Stopping speech");
        _cts?.Cancel();
        // Azure Speech SDK doesn't have a direct stop method for default speaker
        // Cancellation via token is the recommended approach
        return Task.CompletedTask;
    }
}
```

### Configuration in Program.cs

```csharp
builder.Services.AddSingleton<ITextToSpeechService, AzureTextToSpeechService>();
```

### Prerequisites

1. Create an Azure Cognitive Services Speech resource
2. Add credentials to User Secrets or appsettings.json:
```json
{
  "AzureVoiceResourceKey": "your-key-here",
  "AzureVoiceRegion": "australiaeast"
}
```

---

## Example 4: NAudio Audio Playback Service

This example implements real audio playback using NAudio.

### Required NuGet Packages

```xml
<PackageReference Include="NAudio" Version="2.2.1" />
```

### Implementation

```csharp
using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

public class NAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly ILogger<NAudioPlaybackService> _logger;
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFileReader;
    private CancellationTokenSource? _cts;

    public bool IsPlaying { get; private set; }

    public NAudioPlaybackService(ILogger<NAudioPlaybackService> logger)
    {
        _logger = logger;
    }

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("Audio file not found: {FilePath}", filePath);
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        _logger.LogInformation("NAudio: Playing audio from {FilePath}", filePath);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _audioFileReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFileReader);

            var tcs = new TaskCompletionSource<bool>();
            IsPlaying = true;

            _waveOut.PlaybackStopped += (s, e) =>
            {
                IsPlaying = false;
                tcs.TrySetResult(true);
            };

            _waveOut.Play();

            // Wait for playback to complete or cancellation
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NAudio: Playback cancelled");
        }
        finally
        {
            Cleanup();
        }
    }

    public async Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("NAudio: Playing audio from byte array ({Length} bytes)", audioData.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            using var ms = new MemoryStream(audioData);
            using var reader = new WaveFileReader(ms);
            
            _waveOut = new WaveOutEvent();
            _waveOut.Init(reader);

            var tcs = new TaskCompletionSource<bool>();
            IsPlaying = true;

            _waveOut.PlaybackStopped += (s, e) =>
            {
                IsPlaying = false;
                tcs.TrySetResult(true);
            };

            _waveOut.Play();

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NAudio: Playback cancelled");
        }
        finally
        {
            Cleanup();
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("NAudio: Stopping playback");
        _cts?.Cancel();
        _waveOut?.Stop();
        Cleanup();
        return Task.CompletedTask;
    }

    private void Cleanup()
    {
        _waveOut?.Dispose();
        _waveOut = null;
        _audioFileReader?.Dispose();
        _audioFileReader = null;
        IsPlaying = false;
    }

    public void Dispose()
    {
        Cleanup();
        _cts?.Dispose();
    }
}
```

### Configuration in Program.cs

```csharp
builder.Services.AddSingleton<IAudioPlaybackService, NAudioPlaybackService>();
```

---

## Example 5: NAudio Audio Device Service

### Implementation

```csharp
using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

public class NAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<NAudioDeviceService> _logger;
    private AudioDeviceInfo? _selectedDevice;
    private List<AudioDeviceInfo>? _cachedDevices;

    public NAudioDeviceService(ILogger<NAudioDeviceService> logger)
    {
        _logger = logger;
    }

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (_cachedDevices != null)
        {
            return Task.FromResult(_cachedDevices);
        }

        _logger.LogInformation("Enumerating audio input devices");

        _cachedDevices = new List<AudioDeviceInfo>();
        var deviceCount = WaveInEvent.DeviceCount;

        for (int i = 0; i < deviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            _cachedDevices.Add(new AudioDeviceInfo
            {
                Id = i.ToString(),
                Name = capabilities.ProductName,
                IsDefault = i == 0 // First device is typically default
            });
        }

        _logger.LogInformation("Found {Count} audio input devices", _cachedDevices.Count);
        return Task.FromResult(_cachedDevices);
    }

    public AudioDeviceInfo? GetSelectedInputDevice()
    {
        if (_selectedDevice == null)
        {
            // Initialize cache on first access (safe since WaveInEvent operations are synchronous)
            if (_cachedDevices == null)
            {
                _cachedDevices = GetInputDevicesAsync().GetAwaiter().GetResult();
            }
            _selectedDevice = _cachedDevices.FirstOrDefault(d => d.IsDefault);
        }

        return _selectedDevice;
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Selecting audio input device: {DeviceId}", deviceId);

        var devices = await GetInputDevicesAsync();
        _selectedDevice = devices.FirstOrDefault(d => d.Id == deviceId);

        if (_selectedDevice != null)
        {
            _logger.LogInformation("Selected device: {Name}", _selectedDevice.Name);
        }
        else
        {
            _logger.LogWarning("Device not found: {DeviceId}", deviceId);
        }

        return _selectedDevice != null;
    }
}
```

### Configuration in Program.cs

```csharp
builder.Services.AddSingleton<IAudioDeviceService, NAudioDeviceService>();
```

---

## Testing Real Implementations

### 1. Test Whisper STT

```bash
# Start the API with Whisper
dotnet run

# Check logs for "Starting Whisper transcription"
# Speak into the microphone
# Check logs for transcription output
```

### 2. Test Ollama LLM

```bash
# Start Ollama
ollama serve

# Pull model
ollama pull llama2

# Start API
dotnet run

# Trigger response
curl -X POST http://localhost:5141/api/panelist/trigger

# Check logs for "Generating response via Ollama"
```

### 3. Test Azure TTS

```bash
# Set credentials
dotnet user-secrets set AzureVoiceResourceKey "your-key"
dotnet user-secrets set AzureVoiceRegion "australiaeast"

# Start API
dotnet run

# Trigger response
curl -X POST http://localhost:5141/api/panelist/trigger

# Listen for audio output from speakers
```

---

## Combining Real Implementations

To use all real implementations together, register them in `Program.cs`:

```csharp
// Real implementations
builder.Services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
builder.Services.AddSingleton<ILanguageModelService, OllamaLanguageModelService>();
builder.Services.AddSingleton<ITextToSpeechService, AzureTextToSpeechService>();
builder.Services.AddSingleton<IAudioPlaybackService, NAudioPlaybackService>();
builder.Services.AddSingleton<IAudioDeviceService, NAudioDeviceService>();
```

All services will work together seamlessly through the `AIPanelistOrchestrator`.
