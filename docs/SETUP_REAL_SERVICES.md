# Setting Up Real AI Services - Quick Start

This guide shows how to configure and run the AI Panelist with real AI services instead of mock implementations.

## Prerequisites

### Required for All Configurations
- Windows 10/11 (for Windows audio services)
- .NET 10 SDK
- Microphone connected to the host machine
- Speakers or audio output device

### For Whisper STT
- **No additional setup required** - The Whisper model will be automatically downloaded on first run (~150MB for base.en model)
- GPU with CUDA support (optional, but recommended for better performance)

### For Ollama LLM
- **Ollama installed and running** - Download from [https://ollama.ai/download](https://ollama.ai/download)
- Pull a model: `ollama pull llama2` (or your preferred model)
- Ensure Ollama is running: `ollama serve`

### For Azure TTS
- Azure Cognitive Services Speech resource
- Subscription key and region

---

## Quick Configuration Guide

### Option 1: Full Local Stack (Recommended for Testing)

Use Whisper STT + Ollama LLM + Mock TTS + Windows Audio

**Configuration (`appsettings.json`):**
```json
{
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "Model": "llama2"
  },
  "AIPanelist": {
    "SttServiceType": "Whisper",
    "LlmServiceType": "Ollama",
    "TtsServiceType": "Mock",
    "AudioDeviceServiceType": "Windows",
    "AudioPlaybackServiceType": "Windows"
  }
}
```

**Prerequisites:**
1. Install Ollama: https://ollama.ai/download
2. Pull model: `ollama pull llama2`
3. Start Ollama: `ollama serve`
4. Run the API

---

### Option 2: Full Production Stack

Use Whisper STT + Ollama LLM + Azure TTS + Windows Audio

**Configuration (`appsettings.json`):**
```json
{
  "AzureVoiceResourceKey": "<your-key-here>",
  "AzureVoiceRegion": "eastus",
  "AzureVoiceSettings": {
    "VoiceName": "en-AU-WilliamNeural"
  },
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "Model": "llama2"
  },
  "AIPanelist": {
    "SttServiceType": "Whisper",
    "LlmServiceType": "Ollama",
    "TtsServiceType": "Azure",
    "AudioDeviceServiceType": "Windows",
    "AudioPlaybackServiceType": "Windows"
  }
}
```

**Prerequisites:**
1. Set up Azure Speech resource
2. Store credentials in user secrets: 
   ```bash
   cd src/API
   dotnet user-secrets set "AzureVoiceResourceKey" "your-key-here"
   dotnet user-secrets set "AzureVoiceRegion" "eastus"
   ```
3. Install and start Ollama (see Option 1)
4. Run the API

---

### Option 3: Hybrid (Azure TTS only, rest Mock)

Use Mock STT/LLM + Azure TTS for quick audio testing

**Configuration (`appsettings.json`):**
```json
{
  "AzureVoiceResourceKey": "<your-key-here>",
  "AzureVoiceRegion": "eastus",
  "AIPanelist": {
    "SttServiceType": "Mock",
    "LlmServiceType": "Mock",
    "TtsServiceType": "Azure",
    "AudioDeviceServiceType": "Windows",
    "AudioPlaybackServiceType": "Windows"
  }
}
```

---

## Configuration Options

### Service Type Options

| Service | Valid Values | Description |
|---------|-------------|-------------|
| `SttServiceType` | `Mock`, `Whisper` | Speech-to-text service |
| `LlmServiceType` | `Mock`, `Ollama` | Language model for summaries/responses |
| `TtsServiceType` | `Mock`, `Azure` | Text-to-speech service |
| `AudioDeviceServiceType` | `Mock`, `Windows` | Audio input device management |
| `AudioPlaybackServiceType` | `Mock`, `Windows` | Audio output playback |

### Ollama Configuration

```json
{
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "Model": "llama2"
  }
}
```

**Available Models:**
- `llama2` - Good balance of speed and quality (recommended)
- `llama3` - Better quality, slower
- `mistral` - Faster, good for summaries
- Any other Ollama-compatible model

To use a different model:
1. Pull it: `ollama pull <model-name>`
2. Update the `Model` setting in configuration

### Azure TTS Configuration

```json
{
  "AzureVoiceResourceKey": "<your-key>",
  "AzureVoiceRegion": "eastus",
  "AzureVoiceSettings": {
    "VoiceName": "en-AU-WilliamNeural"
  }
}
```

**Available Voices:**
- `en-AU-WilliamNeural` - Australian male (default)
- `en-AU-AnnetteNeural` - Australian female
- `en-US-GuyNeural` - US male
- `en-US-JennyNeural` - US female
- See full list: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support

### Whisper Configuration

```json
{
  "Whisper": {
    "ModelPath": null
  }
}
```

- `ModelPath`: Optional. If not set, Whisper will download and use `ggml-base.en.bin` automatically
- Model is stored in `<app-directory>/Models/ggml-base.en.bin`
- First run will download the model (~150MB)

---

## Running the Application

### 1. Start External Services

**If using Ollama:**
```bash
ollama serve
```

Verify it's running:
```bash
curl http://localhost:11434/api/tags
```

### 2. Configure the API

Edit `src/API/appsettings.json` with your desired service types (see options above).

For Azure credentials, use user secrets:
```bash
cd src/API
dotnet user-secrets set "AzureVoiceResourceKey" "your-key"
dotnet user-secrets set "AzureVoiceRegion" "eastus"
```

### 3. Run the API

```bash
cd src/API
dotnet run
```

On startup, you'll see which services are loaded:
```
info: Program[0]
      AI Panelist Service Configuration:
info: Program[0]
        STT: Whisper
info: Program[0]
        LLM: Ollama
info: Program[0]
        TTS: Azure
info: Program[0]
        Audio Device: Windows
info: Program[0]
        Audio Playback: Windows
```

### 4. Test the System

**List audio devices:**
```bash
curl http://localhost:5141/api/panelist/devices
```

**Trigger a response:**
```bash
curl -X POST http://localhost:5141/api/panelist/trigger
```

**Monitor logs** to see the full pipeline in action.

---

## Troubleshooting

### Whisper STT Issues

**Problem:** "Downloading Whisper model..." takes too long
- **Solution:** Download manually and set `ModelPath` in configuration

**Problem:** Transcription is slow or inaccurate
- **Solution:** 
  - Ensure you have a GPU with CUDA support
  - Try a smaller model if performance is poor
  - Check microphone is properly configured

**Problem:** "Audio device not found"
- **Solution:** Use `/api/panelist/devices` to list available devices and select the correct one

### Ollama Issues

**Problem:** "Could not connect to Ollama"
- **Solution:** 
  - Ensure Ollama is running: `ollama serve`
  - Check endpoint in configuration matches: `http://localhost:11434`
  - Verify firewall isn't blocking port 11434

**Problem:** Responses are too slow
- **Solution:**
  - Use a smaller/faster model: `ollama pull mistral`
  - Update `Model` in configuration
  - Ensure GPU acceleration is working

**Problem:** Model not found
- **Solution:** Pull the model: `ollama pull llama2`

### Azure TTS Issues

**Problem:** "Azure Speech credentials not configured"
- **Solution:** Set credentials in user secrets or appsettings.json

**Problem:** "Azure TTS error: Unauthorized"
- **Solution:** Verify subscription key and region are correct

**Problem:** Voice not found
- **Solution:** Check voice name spelling and availability in your region

### Windows Audio Issues

**Problem:** No audio devices found
- **Solution:** 
  - Check microphone is connected and enabled in Windows Sound settings
  - Run API as administrator if needed

**Problem:** Audio playback not working
- **Solution:**
  - Check speakers are connected and set as default
  - Verify audio files are valid WAV format for filler phrases

---

## Performance Tips

### For Best STT Performance
- Use a GPU with CUDA support
- Use the `base.en` model for English (default)
- Close other applications using the microphone

### For Best LLM Performance
- Use `llama2` or `mistral` models for good balance
- Ensure GPU acceleration is enabled in Ollama
- Adjust `NumPredict` in OllamaLanguageModelService if responses are too long/short

### For Best TTS Performance
- Azure TTS is fast and high-quality (recommended for production)
- Mock TTS is instant for testing the pipeline without network calls

---

## Example: Complete Setup from Scratch

```bash
# 1. Install Ollama
# Download from https://ollama.ai/download and install

# 2. Pull LLM model
ollama pull llama2

# 3. Start Ollama
ollama serve

# 4. In a new terminal, configure the API
cd src/API

# 5. Set Azure credentials (if using Azure TTS)
dotnet user-secrets set "AzureVoiceResourceKey" "your-key-here"
dotnet user-secrets set "AzureVoiceRegion" "eastus"

# 6. Edit appsettings.json to use real services
# Set SttServiceType: "Whisper"
# Set LlmServiceType: "Ollama"
# Set TtsServiceType: "Azure" (or "Mock" for testing)
# Set AudioDeviceServiceType: "Windows"
# Set AudioPlaybackServiceType: "Windows"

# 7. Run the API
dotnet run

# 8. In another terminal, test it
curl http://localhost:5141/api/panelist/devices
curl -X POST http://localhost:5141/api/panelist/trigger

# 9. Watch the logs for the full pipeline
```

The system will now:
1. Capture audio from your microphone via Windows APIs
2. Transcribe it using Whisper (downloading model on first run)
3. Maintain a rolling transcript buffer
4. Generate summaries every 45 seconds using Ollama
5. When triggered, generate a response using Ollama
6. Convert response to speech using Azure TTS (or mock)
7. Play the audio through Windows speakers

---

## Switching Between Configurations

You can switch between mock and real services at any time by editing `appsettings.json` and restarting the API. No code changes required!

**For development/testing:**
- Use mock services for fast iteration
- Use Ollama + Whisper for local testing
- Use Azure TTS only when testing audio quality

**For production/demo:**
- Use full stack with Whisper + Ollama + Azure TTS
- Ensure all external services are running and configured

---

## Next Steps

1. Review logs to ensure services are working correctly
2. Test audio device selection via API endpoints
3. Adjust Ollama model selection for your needs
4. Tune Whisper model size for performance vs accuracy
5. Connect Moderator and Bubbles MAUI apps to test full system

For more details, see:
- `docs/local-pipeline-guide.md` - Complete architecture documentation
- `docs/example-implementations.md` - Implementation details and code examples
- `docs/IMPLEMENTATION_COMPLETE.md` - System overview and success criteria
