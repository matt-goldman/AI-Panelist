# AI Panelist – “Bubbles”

A lightweight, local-first AI “panelist” built for a live Beer Driven Devs event.

Bubbles is not a product.
It’s theatre with guardrails.

It listens (imperfectly) to a live discussion, maintains a rough sense of the room, and speaks only when explicitly invited by the moderator. It has a beer-themed animated presence on an iPad and can be dramatically “overfilled” (overflow animation) if we need to cut it off.

If it works: great.
If it doesn’t: we thank it for its service and move on.

---

## What This Project Is

- A fun hack
- A local AI demo
- A moderated participant in a live panel
- An exploration of AI presence in human discussion

## What This Project Is Not

- A general-purpose conversational system
- An enterprise architecture reference
- An autonomous agent
- A replacement for humans

---

## High-Level Architecture

**Laptop Host (Laptop)**  
- Records mic input (via OBS)
- Runs Whisper STT
- Maintains rolling transcript
- Periodically summarises discussion
- Generates responses via local LLM
- Converts responses to speech (TTS)
- Broadcasts state updates (SignalR)

**Bubbles Display (iPad – .NET MAUI)**  
- Fullscreen animated beer UI
- Shows states: Idle / Listening / Thinking / Speaking / Overflow / Disabled
- No AI logic

**Moderator Control (Phone or MAUI mini-app)**  
- Trigger AI
- Cancel response
- Disable AI

---

## Design Principles

- No autonomous interjections
- Short responses (≤150 words)
- Self-deprecating humour only
- Never attack individuals
- Must be killable instantly
- Failure is acceptable

---

## Operational Flow

1. Panel runs normally
2. STT and summary update in background
3. Moderator triggers Bubbles
4. Bubbles “thinks”
5. Response generated and spoken
6. Return to listening
7. If needed → overflow → disabled

---

## Removing the AI (Scripted Line)

> “Looks like we’ve overfilled it. Bubbles, thanks for joining us tonight.”

---

## Repository Structure

```yaml
/docs
  software-spec.md              # Original requirements
  hardware-setup.md             # Hardware configuration
  prompts.md                    # LLM prompt templates
  local-pipeline-guide.md       # 🆕 Local AI pipeline architecture & developer guide
  example-implementations.md    # 🆕 Real implementation examples (Whisper, Ollama, etc.)

/src
  /API                          # ASP.NET Core coordination layer
    /Services
      /Interfaces               # Service abstractions (STT, LLM, TTS)
      /Implementations          # Mock & real implementations
      AIPanelistOrchestrator.cs # Main pipeline coordinator
      TranscriptBufferService.cs # Rolling transcript buffer
    /Controllers
      PanelistController.cs     # REST API endpoints
    /Hubs
      BubblesHub.cs            # SignalR hub for state broadcasting
  
  /Bubbles                      # .NET MAUI display app (iPad)
  /ModeratorApp                 # .NET MAUI control app (Phone)
  /Shared                       # Shared models and state management
```

## Quick Start

### Running the API

```bash
cd src/API
dotnet run
```

The API starts with **mock implementations** by default for easy testing.

### Triggering a Response

**Via REST API:**
```bash
curl -X POST http://localhost:5141/api/panelist/trigger
```

**Via Moderator App:**
- Set panelist state to "Listening" - the API will automatically trigger a response

### Testing the Pipeline

The system works end-to-end with mock implementations:
- ✅ Continuous mock transcription every 5 seconds
- ✅ Periodic summary generation every 45 seconds
- ✅ Full response generation pipeline (Thinking → Speaking → Listening)
- ✅ State broadcasting via SignalR
- ✅ Cancel/disable functionality

---

## Using Real AI Services

**Real implementations are now available!** 🎉

The system includes production-ready implementations:
- **Whisper.net** - Local STT with Windows audio capture (NAudio)
- **Ollama** - Local LLM inference for summaries and responses
- **Azure Cognitive Services** - High-quality text-to-speech
- **Windows Audio** - Real audio device management and playback

### Quick Setup Guide

**See [SETUP_REAL_SERVICES.md](docs/SETUP_REAL_SERVICES.md) for complete setup instructions.**

**Quick Example - Full Local Stack:**

1. Install Ollama: https://ollama.ai/download
2. Pull model: `ollama pull llama2`
3. Start Ollama: `ollama serve`
4. Edit `src/API/appsettings.json`:
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
5. Run: `dotnet run`

The system will:
- Capture audio from your microphone
- Transcribe with Whisper (auto-downloads model on first run)
- Generate summaries and responses with Ollama
- Broadcast states via SignalR

**Configuration is simple** - just edit `appsettings.json` to switch between mock and real services. No code changes required!

### Documentation

- **[SETUP_REAL_SERVICES.md](docs/SETUP_REAL_SERVICES.md)** - Quick start guide for real AI services ⭐
- **[local-pipeline-guide.md](docs/local-pipeline-guide.md)** - Architecture and developer guide
- **[example-implementations.md](docs/example-implementations.md)** - Complete implementation examples and code

---

## Before You Run (Setup Requirements)

This project uses **Qwen3-TTS** for voice synthesis. A few things to note:

### Voice Cloning (Optional)

The TTS server supports voice cloning from a reference audio sample. You'll need:
- A `.wav` file of the voice you want to clone (~10-30 seconds of clear speech)
- A transcript of that audio

Configure via environment variables `TTS_REF_AUDIO` and `TTS_REF_TEXT`, or edit the defaults in `server.py`.

See [src/QwenAPI/setup-guide.md](src/QwenAPI/setup-guide.md) for detailed setup instructions.

### Pre-generated Audio (Optional)

For the full experience, you can pre-generate:
- **`Intro.wav`** - Intro audio (place in `src/API/wwwroot/audio/`)
- **Filler phrases** - Audio clips for natural pauses (place in `src/API/wwwroot/audio/filler-phrases/`)

### TTS Server (Windows + CUDA)

On Windows, PyTorch CUDA has issues running natively. The AppHost is configured to run the TTS server via **WSL**:

1. Set up WSL with Ubuntu (e.g., `Ubuntu-22.04`)
2. Copy `src/QwenAPI/server.py` and `requirements.txt` to your WSL environment
3. Create a Python venv and install dependencies
4. Update the path in `AppHost.cs` to match your WSL setup

If you're on **Linux or macOS**, edit `AppHost.cs` - there's a commented-out `AddPythonApp` option you can use instead of the WSL executable.

---

## Why We Built This

Because we joked about inviting an AI to the panel…  
And then realised it would be fun.

Cheers 🍻