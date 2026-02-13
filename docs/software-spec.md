# Software Specification – AI Panelist (Bubbles)

## 1. Overview

Bubbles is a moderated AI participant for a live panel event.

It:
- Listens continuously via STT
- Maintains rolling context
- Responds only when triggered
- Speaks via TTS
- Displays animated state on stage

It is intentionally constrained and non-autonomous.

---

## 2. Components

### 2.1 Beast Host

Responsibilities:

- Continuous audio ingestion
- Whisper STT (GPU)
- Rolling transcript buffer (2–3 minutes)
- Periodic summarisation (5–8 bullet points, every 30–60 seconds)
- On trigger:
  - Combine summary + recent transcript + moderator question
  - Generate response
  - Convert to speech
  - Save artefacts (text + optional WAV)
- Broadcast state updates to display app

### Internal Modules

- Audio Capture
- STT Engine
- Transcript Buffer
- Summariser
- Response Generator
- TTS Engine
- State Controller
- SignalR Server

---

### 2.2 Bubbles Display (MAUI)

Responsibilities:

- Fullscreen visual only
- Beer-themed animation base layer
- Optional Lottie overlays
- Large readable status text
- React to state messages

States:

- Idle
- Listening
- Thinking
- Speaking
- Overflow
- Disabled

---

### 2.3 Moderator Control

Functions:

- Trigger AI
- Cancel response
- Disable AI
- Re-enable (optional)

Must be discreet and low friction.

---

## 3. Models

### 3.1 STT
Whisper large-v3 (local GPU)

- Continuous transcription
- Best-effort accuracy
- No diarisation required

---

### 3.2 Summariser Model

- Small instruct-tuned local model
- Output: 5–8 concise bullets
- Overwrites previous summary

---

### 3.3 Response Model

- Strong instruct-tuned local model
- Must follow strict constraints
- Hard cap: 150 words

---

### 3.4 TTS

- Local TTS engine
- Must output WAV
- Must play audio reliably
- Slight synthetic tone acceptable

---

## 4. State Transitions

Idle → Listening → Thinking → Speaking → Listening  
Thinking/Speaking → Overflow → Disabled

Overflow may be triggered:
- Manually
- If time limit exceeded

Disabled persists until manually re-enabled.

---

## 5. Response Constraints

- ≤150 words
- Conversational tone
- Light, generic humour
- No personal attacks
- No moral accusations
- No claims of sentience
- Acknowledge partial understanding briefly if needed

---

## 6. Logging (Recommended)

Log:
- Timestamp
- Transcript chunks
- Summary
- Prompt
- Response
- State transitions

Useful for:
- Post-event analysis
- Blog post
- Repo transparency

---

## 7. Latency Targets

- STT near real-time
- Response generation ≤10 seconds
- TTS ≤3 seconds
- Total response time acceptable within conversational pause
