# Architecture Diagrams

These diagrams describe the AI Panelist system using the [C4 model](https://c4model.com/), rendered with [Mermaid's C4 extensions](https://mermaid.js.org/syntax/c4.html).

Everything runs on a single local machine, except two .NET MAUI client apps — **Bubbles** (tablet visual display) and the **Moderator App** (phone control panel) — which connect over the local network via SignalR and HTTP.

Audio capture and playback both happen server-side: room microphones pick up the human panelists, and the AI's synthesised speech plays through the room's speakers. No audio passes through the client apps.

There is no voice activity detection. A human moderator decides when the AI should respond by pressing **Listen** in the Moderator App, which triggers the full pipeline: STT transcript + summary → LLM response → TTS → audio playback. Speech-to-text runs continuously in the background to maintain a rolling transcript and periodic summary, so context is always available when a response is triggered.

## System Context (C4 Level 1)

```mermaid
C4Context
    title AI Panelist — System Context

    Person(human, "Panelist / Audience", "Speaks into room microphone(s) during the panel discussion")
    Person(mod, "Moderator", "Decides when the AI panelist should respond")

    Boundary(local, "Local Machine") {
        System(api, "API Orchestrator", ".NET API — captures audio, coordinates STT / LLM / TTS, plays synthesised speech")
        System(whisper, "Whisper", "Speech-to-text (local)")
        System(ollama, "Ollama", "LLM inference (local)")
        System(qwen, "Qwen3-TTS", "Text-to-speech (Python FastAPI, local)")
    }

    System_Ext(bubbles, "Bubbles App", ".NET MAUI — tablet visual display (Lottie animations)")
    System_Ext(modapp, "Moderator App", ".NET MAUI — phone control panel")

    Rel(human, api, "Speaks into room microphone(s)")
    Rel(mod, modapp, "Triggers / cancels / configures")
    Rel(modapp, api, "HTTP + SignalR")
    Rel(api, bubbles, "SignalR state updates")
    Rel(api, whisper, "Transcription requests")
    Rel(api, ollama, "Prompt + response")
    Rel(api, qwen, "TTS synthesis")
```

## Pipeline Flow

The sequence below shows how a single response is produced. Note that STT runs continuously in the background — the trigger from the moderator initiates LLM generation using the transcript already accumulated.

```mermaid
sequenceDiagram
    participant MOD as 👤 Moderator
    participant MODAPP as Moderator App
    participant API as .NET API
    participant WHISPER as Whisper (STT)
    participant OLLAMA as Ollama LLM
    participant QWEN as Qwen3-TTS
    participant BUBBLES as Bubbles (tablet)

    Note over API,WHISPER: Background: continuous STT builds a<br/>rolling transcript + periodic LLM summary

    MOD->>MODAPP: Presses "Listen"
    MODAPP->>API: SignalR: UpdatePanelState(Listening)
    API->>BUBBLES: State → Listening
    API->>MODAPP: State → Listening
    API->>API: TriggerResponseAsync()

    API->>WHISPER: Pause transcription
    API->>BUBBLES: State → Thinking
    API->>MODAPP: State → Thinking

    Note over API: Plays filler phrase while LLM generates

    API->>OLLAMA: Generate response (summary + recent transcript)
    OLLAMA-->>API: Response text

    API->>QWEN: Synthesise speech
    QWEN-->>API: Audio

    API->>BUBBLES: State → Speaking
    API->>MODAPP: State → Speaking
    Note over API: Plays audio through room speakers

    API->>BUBBLES: State → Listening
    API->>MODAPP: State → Listening
    API->>WHISPER: Resume transcription

    rect rgb(255, 230, 230)
        Note over MOD,BUBBLES: Moderator can cancel or disable at any time
        MOD->>MODAPP: Cancel / Disable
        MODAPP->>API: POST /cancel or /disable
        API->>BUBBLES: State → Idle
        API->>MODAPP: State → Idle
    end
```
