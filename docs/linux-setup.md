# Running Bubbles on Linux (PipeWire + StreamYard)

The original build assumed Windows: NAudio for capture and playback, VoiceMeeter/VB-Cable
for routing, and WSL to get CUDA working for Qwen3-TTS. This document covers the Linux
port, which replaces all three.

What the move fixes for free:

- No more Nahimic reboots.
- Qwen3-TTS runs natively — no WSL hop.
- Routing is a real audio graph you can inspect (`qpwgraph`) instead of a virtual cable
  you have to believe in.

---

## 1. Prerequisites

```bash
# PipeWire itself, the Pulse compatibility layer, and the CLI tools we shell out to
pactl --version      # from libpulse / pipewire-pulse
pw-cat --version     # from pipewire (pipewire-audio / pipewire-utils, distro-dependent)
pactl info           # should print "Server Name: PulseAudio (on PipeWire x.y.z)"

# Optional but very useful for eyeballing the graph
qpwgraph
```

If `pw-cat` is missing, install your distro's PipeWire utilities package
(`pipewire-audio` on Arch/CachyOS, `pipewire-bin` on Debian/Ubuntu).

You also need:

- **.NET 10 SDK** — `dotnet --list-sdks`
- **NVIDIA driver + CUDA** for Qwen3-TTS — `nvidia-smi`
- **Python 3.10+** with a venv for the TTS server

---

## 2. Build the audio graph

```bash
./scripts/bubbles-audio.sh up
```

This creates:

| Node | Kind | Purpose |
|---|---|---|
| `bubbles-tab-sink` | null sink | The StreamYard browser tab plays into this |
| `bubbles-tab-sink.monitor` | source | What Whisper captures — the whole host mix |
| `bubbles-tts-sink` | null sink | Bubbles' TTS plays into this |
| `bubbles-mic` | remapped source | Shows up in the browser as **"Bubbles Mic"** |

It also loops `bubbles-tab-sink.monitor` back to your real output device, otherwise the
hosts' audio disappears into the null sink and you hear nothing. Override the destination
with `MONITOR_SINK=<sink name>`, or skip it with `NO_LOOPBACK=1`.

Other commands:

```bash
./scripts/bubbles-audio.sh status   # what exists right now
./scripts/bubbles-audio.sh route    # move a browser tab's audio into bubbles-tab-sink
./scripts/bubbles-audio.sh down     # tear it all down
```

### Why capture the tab and not a mic

For this event only Matt's mic is local — the other hosts are remote and exist *only*
inside the StreamYard tab mix. Direct mic-node capture (which worked in January when
everyone was in the room) would miss them entirely. So we capture the tab's own output.

We never read from Twitch. That's the broadcast domain: post-encode, post-CDN, 3–8s
behind, and fatal for conversational response. Everything here stays inside the local
PipeWire graph.

---

## 3. Route the StreamYard tab into the capture sink

Audio routing per-application is a runtime thing — the browser has to be playing audio
before it shows up as a stream to move.

1. Open the StreamYard studio tab and make sure something is making noise.
2. Run `./scripts/bubbles-audio.sh route` and pick the browser's sink input.

Or do it by hand:

```bash
pactl list short sink-inputs                  # find the browser stream
pactl move-sink-input <id> bubbles-tab-sink
```

Or point-and-click in `pavucontrol` → Playback tab → set the browser's output device to
"Bubbles Tab Capture".

> **Gotcha:** browsers sometimes create a new sink input when audio starts/stops, which
> snaps back to the default sink. Check `./scripts/bubbles-audio.sh status` right before
> going live, and re-route if needed.

---

## 4. Add the AI as its own StreamYard guest

Bubbles joins as a second guest rather than being mixed into your feed, so it gets its own
tile and its own audio track.

1. Generate a StreamYard guest invite link.
2. Open it in a **separate browser profile or window** (not another tab of the same
   session — StreamYard ties devices to a session).
3. In that guest's device picker:
   - **Microphone:** "Bubbles Mic"
   - **Camera:** the avatar/static card (or camera off with an image)
4. Confirm the level meter on the guest tile moves when Bubbles speaks.

> **Validate this before the week of the event.** The one thing no amount of local testing
> proves is whether StreamYard's picker lists the virtual source. It's a normal
> `module-remap-source`, not a monitor, so browsers should treat it as an ordinary
> microphone — but check it with your actual browser and StreamYard account.

Bubbles' voice reaching the Twitch stream happens because it went *into* StreamYard as a
guest, not because anything was fed back from Twitch.

---

## 4a. Rehearsing without StreamYard

You do **not** need a live broadcast, a second person, or even a StreamYard account to
test most of this. In rough order of fidelity:

### Option 1 — StreamYard solo (highest fidelity, free)

You can open a studio without going live, and invite yourself as a guest from a second
browser profile. That gives you the real device picker, the real two-guest topology, and
the real return path, with nobody else involved and nothing broadcast.

### Option 2 — Jitsi Meet (no account at all)

`meet.jit.si` needs no signup. Join the same room twice: Firefox as "the hosts",
a separate Chromium profile as "Bubbles" using **Bubbles Mic**. Proves the picker, the
guest topology, and the echo return path through a real conferencing service.

### Option 3 — Fully offline rig (no service, no network)

The script can simulate both halves of the loop:

```bash
# Stand in for the remote hosts: play a recorded panel discussion into the capture sink
./scripts/bubbles-audio.sh play ~/recordings/panel.wav

# Simulate Bubbles' voice returning through StreamYard's mix, 300ms late
./scripts/bubbles-audio.sh echo on
ECHO_DELAY_MS=500 ./scripts/bubbles-audio.sh echo on   # or pick your own delay
./scripts/bubbles-audio.sh echo off
```

`echo on` is what makes the self-suppression gate testable: without the gate, Bubbles'
own words show up in the transcript a few hundred milliseconds after it speaks. With the
gate, they don't. That is the whole test.

> **This makes noise.** The graph deliberately loops the capture sink to your speakers so
> you can hear the hosts, which means test audio comes out of them too. For silent
> testing, bring the graph up without that loopback:
> ```bash
> ./scripts/bubbles-audio.sh down
> NO_LOOPBACK=1 ./scripts/bubbles-audio.sh up
> ```

### Testing the self-suppression gate

Bubbles' voice goes into StreamYard as a guest and comes back to us inside the captured
tab mix. Left alone, Bubbles transcribes itself, feeds its own words into its own context,
and can self-trigger. The gate stops that.

It works on the **transcript**, not the microphone: capture never stops (interrupting a
PipeWire node mid-event causes buffer glitches and loses whatever a host said meanwhile).
Instead, audio whose wall-clock window overlaps a period when Bubbles was speaking is
dropped on its way into Whisper.

Two details worth knowing when you read the logs:

- Suppression opens when **audio actually starts playing**, not when the moderator presses
  the button. The hosts may still be talking while Bubbles thinks, and that's transcript
  worth keeping.
- A 10 second capture window containing a 3 second response is **split**, not discarded —
  the host speech either side of it still gets transcribed. Without this you'd be blind
  for up to 10 seconds after every answer, which is exactly when a host replies to it.

To see it work:

```bash
./scripts/bubbles-audio.sh echo on     # Bubbles hears itself, 300ms late
# trigger a response, then watch the log:
#   Self-suppression opened (tts playback)
#   Dropped 12.4s of self-audio from Monitor of Bubbles Tab Capture before transcription
#   Self-suppression closed (tts playback): 12.4s of audio suppressed, plus a 350ms tail
```

Then confirm Bubbles' own words are absent from the transcript, and that a host speaking
immediately afterwards still lands.

To see the problem it solves, set `SelfSuppression:Enabled` to `false` and trigger again
with `echo on` — Bubbles' answer will appear in its own transcript.

Tuning `SelfSuppression:TailMs` (default 350, useful range 200–500): raise it if Bubbles'
last few words leak into the next window; lower it if host speech immediately after a
response is being swallowed.

### What each option can't prove

| | Mic picker | Guest slot | Echo path | StreamYard-specific behaviour |
|---|---|---|---|---|
| StreamYard solo | ✅ | ✅ | ✅ | ✅ |
| Jitsi / Teams | ✅ | ✅ (Jitsi) | ✅ | ❌ |
| Offline rig | ❌ | ❌ | ✅ (simulated) | ❌ |

---

## 5. Configure the API

`src/API/appsettings.Development.json` is already set up for the event:

```json
{
  "AIPanelist": {
    "SttServiceType": "Whisper",
    "LlmServiceType": "OllamaAspire",
    "TtsServiceType": "Qwen3-TTS",
    "AudioDeviceServiceType": "PipeWire",
    "AudioPlaybackServiceType": "PipeWire"
  },
  "PipeWire": {
    "DefaultCaptureNode": "bubbles-tab-sink.monitor",
    "OutputSink": "bubbles-tts-sink"
  }
}
```

`AudioDeviceServiceType` also selects the capture backend: `PipeWire` uses `pw-cat`,
`Windows` uses NAudio. Setting `Windows` on a non-Windows host fails at startup with a
clear message rather than at the first audio callback.

**If `OutputSink` is null, Bubbles talks out of your laptop speakers instead of into
StreamYard.** That is the single most likely misconfiguration.

Device IDs are PipeWire `node.name` values (column 2 of `pactl list short sources`).
They're stable across reboots; indexes are not.

### Selecting capture nodes at runtime

The moderator app's device list is populated from `GET /api/devices`, which enumerates
PipeWire sources fresh on every call — so nodes created after the API started (including
the ones from `bubbles-audio.sh`) show up without a restart.

```bash
curl -s localhost:5141/api/devices | jq -r '.[] | "\(.id)\t\(.name)"'
curl -X POST localhost:5141/api/devices/select/bubbles-tab-sink.monitor
```

---

## 5a. Streamed responses and the LLM

The metric that matters is **time-to-first-audio**, not total generation time. January's
problem was the silence after the trigger, not the trigger itself.

Instead of generating the whole answer, synthesising it, then playing it, the streamed path
flushes to TTS at clause boundaries while the model is still writing, and queues chunk N+1
while chunk N is still playing.

```
Time to first audio: 521ms (first chunk: "That's a fascinating point.")
Streamed response complete: 4 chunks, first audio at 521ms, 19362ms total
```

Chunks are cut on punctuation, never on token count — a fixed-size split lands mid-phrase
and Qwen3 renders bad prosody at the seam. The first chunk is kept short because it sets
time-to-first-audio, but not so short that it comes out flat; later chunks are longer,
since playback of the previous one is buying time.

**Filler phrases are gone from this path, deliberately.** They existed to cover the gap
between trigger and answer. The first real clause now fills that gap coherently, and a
canned "Great question!" would collide with it. They are still there on the non-streamed
path, which is what `StreamingResponse:Enabled: false` falls back to.

### Choosing the model at runtime

Set `AIPanelist:LlmServiceType` to `ChatClient` and pick a provider. This is the only path
that streams; the older `Ollama` / `FoundryLocal` options still work but generate the whole
response before any audio plays (startup logs a warning if you do that with streaming on).

```json
"AIPanelist": { "LlmServiceType": "ChatClient" },
"ChatClient": {
  "Provider": "Ollama",              // Ollama | OpenAI | AzureAIInference
  "Model": "gpt-oss:20b",
  "Endpoint": "http://localhost:11434"
}
```

`OpenAI` also covers any OpenAI-compatible endpoint via `ChatClient:Endpoint`. Put
`ChatClient:ApiKey` in user-secrets, never in appsettings:

```bash
cd src/API && dotnet user-secrets set "ChatClient:ApiKey" "sk-..."
```

**Reasoning models:** streaming reads only the answer channel. Microsoft.Extensions.AI
surfaces reasoning as `TextReasoningContent`, separate from `TextContent`, so a model that
thinks before answering doesn't gate time-to-first-audio on the whole thinking phase —
whatever the provider. Reasoning tokens are counted and discarded; the log line at the end
of a response tells you how many.

---

## 6. TTS server

Qwen3-TTS runs natively now. Create a venv next to `server.py`:

```bash
cd src/QwenAPI
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
pip install qwen-tts torch soundfile     # plus whatever the fork needs
```

The AppHost launches it for you on Linux (`bash -c "source .venv/bin/activate && exec
python server.py"`). Override the paths in AppHost configuration if your venv lives
elsewhere:

| Setting | Default |
|---|---|
| `QwenTts:WorkingDirectory` | `../../src/QwenAPI` |
| `QwenTts:VenvPath` | `.venv` |
| `QwenTts:RefAudio` | server default (`~/qwentts/bubbles-sample.wav`) |
| `QwenTts:RefText` | server default |

`/health` now returns **503 until the model has compiled and warmed up** (~2 minutes from
cold, faster with the torch inductor cache warm), so Aspire's `WaitFor` blocks on a
genuinely usable server rather than just an open port.

Run everything:

```bash
dotnet run --project Aspire/AI-Panelist.AppHost
```

> The MAUI projects (`Bubbles`, `ModeratorApp`, `UI-Common`) do not build on Linux — they
> target iOS/MacCatalyst among others. Build the AppHost or `src/API/API.csproj` directly
> rather than the whole solution.

---

## 7. Troubleshooting

**No transcript at all.**
Check something is actually playing into the capture sink:
```bash
timeout 3 pw-cat --record --target bubbles-tab-sink.monitor --rate 16000 --channels 1 \
    --format s16 --raw - | wc -c     # ~96000 bytes for 3s; near zero means nothing routed
```
If that's silent, the browser isn't routed — see step 3.

**Bubbles comes out of the speakers instead of StreamYard.**
`PipeWire:OutputSink` is null or names a sink that doesn't exist. Verify with
`./scripts/bubbles-audio.sh status`.

**"Bubbles Mic" doesn't appear in the browser.**
The browser needs mic permission for the StreamYard origin before it will enumerate device
names at all. Grant permission, then reopen the picker. Also confirm the source exists:
`pactl list short sources | grep bubbles-mic`.

**Transcript is full of plausible text that nobody said.**
Almost certainly capture attached to the wrong node. `pw-cat --target <name>` takes a
**PipeWire node name**, and a sink's monitor is not one — `bubbles-tab-sink.monitor` is a
PulseAudio compatibility name. pw-cat does not fail on an unresolvable target; it silently
falls back to the default source, so you end up transcribing the room microphone while
every log line looks healthy.

The app handles this: a target ending in `.monitor` is translated to the sink node plus
`-P stream.capture.sink=true`, and startup logs an error if the node doesn't exist. If you
are testing by hand, use the same form:

```bash
# WRONG - silently records the default source
pw-cat --record --target bubbles-tab-sink.monitor --raw -

# RIGHT - records the sink's monitor
pw-cat --record --target bubbles-tab-sink -P stream.capture.sink=true --raw -
```

Diagnose by measuring amplitude, never byte count: bytes arrive whatever you are attached
to. Digital silence (peak exactly 0) means you are correctly attached to an idle null sink;
a low but non-zero floor (~0.0001) means you are listening to a quiet room.

**pw-cat exits immediately.**
Usually a bad `--target`. Node names are case-sensitive and must match `pactl list short
sources` exactly. The capture source auto-restarts on failure (1s backoff) and logs the
node name it tried.

**Audio graph survived a reboot / duplicated itself.**
`pactl load-module` state is per-session and not persistent. Re-run
`./scripts/bubbles-audio.sh up` after a reboot; it's idempotent and won't create duplicates.
