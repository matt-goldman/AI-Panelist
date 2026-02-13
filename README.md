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

**Beast Host (Laptop)**  
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
software-spec.md
hardware-setup.md
prompts.md

/src
BeastHost/
BubblesDisplay/
ModeratorControl/
```

## Why We Built This

Because we joked about inviting an AI to the panel…  
And then realised it would be fun.

Cheers 🍻