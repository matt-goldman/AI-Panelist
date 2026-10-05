# AI Panelist — Backlog v3

Written after the second outing (StreamYard → Twitch). It worked end to end: Bubbles had its
own guest presence, spoke to the audience and heard the other hosts, and the laptop didn't
reboot. Responses were markedly better on Qwen3.8-27B than gpt-oss:20b — six months is a long
time in local models.

Two things failed, and both have concrete causes worth chasing before adding features.

**Principle carried over from v2:** new thing in front, proven thing behind it, hard toggle
between. Nothing here should be able to take the live path down.

---

## The quality bar, and when it changes

Worth stating plainly, because it decides how much of this list is optional.

The README frames Bubbles as a gimmick — *"not a product, theatre with guardrails"* — and
that framing is doing real work. When something goes wrong on the night, everyone laughs and
the panel moves on. Failure tolerance is a **design input** here, not an oversight, and it is
what makes the hacky, guerilla shape of the thing viable and, frankly, part of its charm.

**That changes the moment the context does.** A serious conference submission, or any use
where someone is relying on it, and the same code is held to a standard it does not currently
meet. As it stands this is not a robust, reliable, tested system. It can be — nothing here is
hard — but it is work that has not been done.

Concretely, "reliability matters" would promote these from *nice* to *required*:

- Item 4 (never go silent) — currently the only true live-failure guarantee, and it doesn't exist.
- Item 8a (know when it has stopped) — no operator feedback loop today.
- Item 9 (setup as software) — setup currently depends on memory and attention.
- Item 10 (test tooling) — there is no test project in the repo.
- Plus redundancy that isn't on this list at all: a second machine, a fallback TTS path
  already wired, a rehearsed degraded mode.

Nothing above needs doing *now*. The point is to notice the day the bar moves, rather than
discovering it live.

---

## Built since this was written

So the rest of the list reads accurately. None of it has been through a live event yet.

- **Item 4 (never go silent)** — a canned line whenever a response produces nothing: empty
  answer, LLM error, or no answer text for N seconds. Lines are synthesised at startup and
  cached, so they survive the TTS failing. The empty-response failure was reproduced first.
- **Item 3 (turn down the thinking)**, as response triage — every answer starts with
  reasoning off; the model asks for thinking time only when it needs it, and a spoken
  holding line covers the wait. First answer token went from 10–16s to under a second on
  the ordinary questions, and the two empty answers in the test set disappeared.
- **Item 5 (spoken triggers)** — a second Whisper worker per device on a short rolling
  window, so a phrase fires within about a second instead of the 5–10s the transcript loop
  would take. Off by default.
- **Item 7 (a mouth that means something)** — the API publishes an RMS envelope per chunk
  with a schedule; both displays replay it on their own clock. Shared drawing lives in
  `Bubbles.Visuals` so the MAUI and GTK displays cannot drift apart. The MAUI app pulses
  the Lottie by default and can switch to the bar meter.
- **Item 8 (look things up)** — whole-session transcript with keyword search, offered to
  the model as a tool. Off by default.
- **Item 9 (setup as software)** — *the dashboard half only.* See item 9 for what is
  actually wanted.
- **Item 8a, partly** — the hub now tracks connected displays and readiness fails when
  none are attached. The moderator-app notification on a dropped connection is **not**
  done, and neither is the dashboard on a second machine.

Still untouched: items 1, 2, 2a, 6, 10, 11.

---

## P0 — Fix what broke, and make it measurable

### 1. Instrument the response path properly

Everything below is guesswork until this exists. Right now the only timing we emit is
`Time to first audio: NNNms`, which tells us *that* it was slow, never *where*.

Emit one timeline per response, with elapsed-since-trigger at each hop:

- trigger received
- context snapshot taken (summary + recent transcript)
- LLM request dispatched
- **first answer token received** ← splits "waiting for the model" from "our pipeline"
- first chunk closed by the chunker
- first chunk synthesis complete
- first audio sample played
- each subsequent chunk: closed → synthesised → queued → started playing
- response complete

Also record, at trigger time: **whether a summary request was in flight**, and how long it had
been running. That single field probably answers item 2 on its own.

Add reasoning-token counts to the same timeline — they're the input to item 3.

ServiceDefaults already wires OpenTelemetry, so `Activity` spans give this for free in the
Aspire dashboard rather than needing a bespoke log format.

### 2. Time-to-first-audio — find the actual contention

**The hypothesis that a response waits for the in-flight summary to finish is right about the
symptom but wrong about the mechanism, and the distinction changes the fix.**

What the code actually does, verified:

- `_summaryLock` is held only long enough to assign the finished string. It is never held
  across the LLM call, so a response reading the summary cannot block behind one being written.
- The design intent — *recent verbatim window + last completed summary, never wait for the
  current one* — is already what the code does structurally.
- `GenerateSummaryCallback` skips starting a new summary when `_isResponseInProgress`, but
  cannot stop one already running.
- **`GenerateSummaryAsync(transcript)` is called with no `CancellationToken`.** An in-flight
  summary is uninterruptible.

So the queue is almost certainly **in the inference server, not in us**. One model, one GPU: a
summary generating up to 500 tokens on a 27B occupies it, and a trigger landing mid-summary
waits behind it.

Ordered by cost:

1. **Measure it first** (item 1). Confirm the correlation between "summary in flight at
   trigger" and a slow response before changing anything.
2. **Cancel the in-flight summary when a trigger arrives.** Plumb a `CancellationToken`
   through `GenerateSummaryAsync` and cancel on trigger. The response always wins — a stale
   summary is fine, a slow answer is not.
3. **Suppress summarisation across the whole response**, not just generation. Extend the
   window to cover Speaking too, plus a cooldown.
4. **Stop co-locating the two workloads.** See item 2a — this is the structural fix.

### 2a. Inference runtime and topology

The hardware is comfortably capable of running summarisation and response generation
concurrently. Ollama is what's stopping it, and there are four ways out, in rough order of
effort:

- **Point summaries at a different endpoint.** There's a second inference box on the LAN. The
  config-driven `IChatClient` already makes the endpoint a setting, so this is nearly free —
  it needs a *second* client registration (summary vs response) rather than the single one
  today. Cheapest structural fix by a distance, and it removes contention completely rather
  than mitigating it.
- **A small local model for summaries** on the same box. Never contends with the 27B, costs
  VRAM, and there's headroom on a 5090.
- **Swap the runtime.** llama.cpp's server supports continuous batching and `--parallel`;
  vLLM is built for concurrent requests and would handle both workloads properly. More setup,
  best ceiling.
- **Frontier for one or both.** Removes local contention entirely at the cost of a network
  dependency on the critical path — the trade-off already recorded in v2.

Check `OLLAMA_NUM_PARALLEL` and keep-alive regardless, so the model isn't unloaded between
turns.

### 3. Turn down the thinking

Qwen3.8-27B thinks noisily by default. Every reasoning token is latency the audience hears as
silence, and reasoning is discarded rather than spoken — so it is pure cost unless it's
actually improving the answer.

- Measure the reasoning/answer token split per response first (item 1).
- Then trial a reduced thinking budget, or the model's non-thinking mode, against a fixed
  question set — quality *and* time-to-first-audio, not just speed.
- v2's position was "keep thinking on but bounded" because the quality delta justified the
  panelist. That still holds; **bounded** is the part that was never actually implemented.
- This also feeds item 4: an unbounded thinking budget is the leading suspect for the empty
  answer.

### 4. Never go silent: guard the empty response

**The failure mode:** an audience member asked Bubbles to say *"To be or not to be, that is
the question"* with *"Alt.NET"* after every word. Bubbles didn't refuse — it produced nothing
at all, twice.

**Likely cause (needs reproducing).** Qwen3.8 is a reasoning model and `MaxOutputTokens` is
800 for responses. A fiddly word-by-word transformation is exactly the kind of task that burns
a large thinking budget. If reasoning consumes the budget, `content` comes back empty, the
chunker gets nothing, and zero chunks reach the TTS.

Reproduce with the stack up:

```bash
curl -s http://localhost:11434/api/chat -H 'Content-Type: application/json' -d '{
  "model":"qwen3.8:latest","stream":false,
  "options":{"num_predict":800,"temperature":0.7,"top_p":0.9},
  "messages":[{"role":"user","content":"Say '\''To be or not to be, that is the question'\'' with '\''Alt.NET'\'' after every word."}]
}' | python3 -c "import sys,json;m=json.load(sys.stdin)['message'];print('answer',len(m.get('content') or ''),'thinking',len(m.get('thinking') or ''))"
```

If `answer 0` and `thinking` is large, that's it.

**Fix the robustness regardless of cause.** `StreamingSpeechPipeline` currently completes
happily with zero chunks: the reader loop never runs, `onFirstAudio` never fires, and the
orchestrator logs `Response spoken in 0 chunks:` and returns to Listening. Nothing is spoken
and nothing is surfaced. On stage that reads as Bubbles ignoring someone.

- If the answer stream yields no speakable text, say something — a short generic line, spoken
  through the normal path. Silence is the one unacceptable outcome.
- Log it at error level with the reasoning-character count.
- Raise `MaxOutputTokens`, and cap thinking where the provider exposes a separate budget.

---

## P1 — The features that change the act

### 5. Spoken trigger phrases

Carried from v2 items #7 and #8 — the design work is done and still stands, it was simply
never built. Matt stays the intent authority and converts messy host address into a known
phrase; the button remains the silent fallback.

**New dependency discovered this time, and it's the hard part.** The current STT loop is:

```
Task.Delay(5000)          // process every 5 seconds
SegmentSamples = 16000*10 // in 10-second windows
```

So a phrase can be recognised **5–10 seconds after it's spoken**. That is unusable as a
trigger — worse than the button by a wide margin.

**The fix is to decouple trigger detection from the transcript pipeline entirely**, rather
than shortening the existing loop and paying that cost on every segment. They want different
things: the transcript wants long windows for accuracy, the trigger wants short windows for
latency. Options, all viable:

- A second STT worker on its own thread with ~1s hops and a rolling buffer of the last few
  seconds, doing nothing but matching cores.
- A separate process, or a separate machine.
- **The phone already running the moderator app.** It only has to stream recognised words and
  keep a rolling window of ~10 to check against the cores — well within on-device STT, and it
  puts the microphone next to the person saying the trigger.

The matcher itself is easy:

- Sliding buffer of the last few seconds, normalise both sides, substring match on cores.
- Anchor every core on "Bubbles" + a direction word so third-person mentions don't fire.
- Match on finalised segments only; consumed-marker so one utterance fires once.
- **Read the gated stream**, the same one self-suppression already filters — not a raw tap.

Keep 5–8 cores, 2–3 as habitual go-tos. Exact substring first; only add fuzzy matching if
testing shows Whisper mangling the specific phrases in Matt's voice.

### 6. Multiple distinct input channels (required for in-person)

The biggest blocker for a conference/live event, where the value is per-speaker attribution
rather than one blended mix.

The wireless mic dongles each present as **one device carrying two mics** — almost certainly
stereo, one mic per channel. So the job is splitting a stereo source into two mono sources:

```bash
pactl load-module module-remap-source source_name=panelist-1 \
  master=<dongle-source> channel_map=mono master_channel_map=front-left
```

Same `module-remap-source` machinery `bubbles-audio.sh` already uses for the virtual mic.

**Most of the app side already exists** and was built for exactly this: multi-device capture
with one `DeviceCapture` per source, per-device `SpeakerName` attribution flowing into the
transcript, and the moderator app's device page for naming channels. Untested on Linux —
the `PipeWireAudioDeviceService` enumerates and selects, but multi-device capture hasn't been
exercised since the port.

Decision recorded: **bring our own audio rather than integrating with a venue's desk.** Two
mics per panelist is an acceptable ask. Integrating with in-house systems is a different
project and not one to attempt live.

### 7. A mouth that means something

The current bar meter is shaped to look like speech but isn't driven by it — the app knows
*that* it's speaking, not how loud. Making it real needs amplitude published from the API,
because TTS audio never passes through the display app.

- Compute an RMS envelope per ~50ms frame in `StreamingSpeechPipeline` as PCM is written, and
  publish it over the hub. Low bandwidth, and the display just renders what arrives.
- **Bridging animation between chunks.** The pipeline knows when a chunk has finished playing
  and the next hasn't started — publish that as a sub-state so the display can show "still
  going" rather than dropping to idle mid-answer. This directly covers the chunk-gap problem
  and is cheaper than eliminating the gaps.
- Return to idle only on genuine completion.

Worth doing before chasing the chunk pauses themselves: a visible "still talking" cue makes a
300ms seam read as natural rather than broken.

### 8. Let Bubbles look things up

Every response is already captured, and the rolling transcript exists. When someone asks about
something said earlier in the panel, Bubbles should be able to find it rather than rely on
whatever survived summarisation.

**Deliberately unfancy: plain text and string matching, not embeddings.** A panel is one to two
hours of transcript. Substring and keyword search over that is instant and, more importantly,
debuggable live — you can see exactly why it matched. Semantic search adds a model, a failure
mode, and latency for a corpus small enough not to need it.

Shape is open — SQLite, a database wired up through Aspire, something like Cabinet, or grep
over the captured text files. Let item 10 answer it rather than guessing now.

Exposed to the model as a tool call, which means the response path gains a round trip when
it's used — so it interacts directly with item 2. Measure before assuming it's affordable.

### 8a. Know when it has stopped

Both outings had the same shape of problem: something stopped working and **the operator was
the last to find out.**

On the first night the laptop was on the floor with its screen asleep, silently rebooting.
An audience member spotted it and tried to signal; the only way to discover it was to walk
over and look. On the second, the fault was subtler but the pattern held — the feedback loop
runs through watching logs on the machine you're least able to watch.

Two cheap fixes, both of which would have caught the first night:

- **The moderator app should raise a local notification when the SignalR connection drops.**
  It already tracks connection state — `ConversationStateService` fires
  `ConnectionStateChanged` and has a full reconnect ladder — it just never surfaces it. The
  phone is in your hand; the laptop is not. A dropped hub connection is the single best proxy
  for "something has gone wrong back there", and it catches a reboot, a crash, and a network
  fault identically.
- **Run the Aspire dashboard on a second machine.** No reason not to. It's already there,
  it's already collecting, and a laptop running the pipeline is the worst possible place to
  display its own health. Pairs with item 1 — instrumentation you can't see during the event
  is instrumentation you'll read afterwards and wish you'd seen.

Neither needs new infrastructure. Both are about putting existing signal where the operator
actually is.

---

## P2 — Tooling, and the second screen

### 9. Setup as software, not discipline — the setup wizard

**A project in its own right, not a feature.** What exists today is the dashboard half of
this: a web app at `/setup` with readiness checks, live input meters, device naming, the
audio-graph actions and a speak-and-watch test. That is useful, and it is not the idea.
The idea is a **wizard**: something that takes you through a run from nothing to ready,
asking only what differs this time and verifying each answer before moving on.

**The principle: move setup from discipline to automation.** The run sheet worked, but it
is a checklist held together by memory and attention on the one night attention is
scarcest.

**It has to be adaptive, not a static script.** The two outings so far needed materially
different setups and the next in-person one will differ again. Anything that changes
between runs becomes a question; everything else is checked rather than asked.

**The first question is the one that changes everything else: online or in person.** This
is not a cosmetic branch — the whole audio topology differs, in both directions:

| | Online (StreamYard) | In person |
|---|---|---|
| Capture | The browser tab's sink monitor, because the remote hosts only exist inside the tab mix | The panelists' microphones, one source per speaker (item 6) |
| Bubbles' voice out | `bubbles-tts-sink` → `bubbles-mic`, picked as the guest's microphone | The default output, into the PA |
| Hearing Bubbles | Only via the monitor loopback, because the voice goes to the stream | Unavoidable — the room is carrying it |
| Hearing the hosts | Needs the tab-sink loopback | The room |

Today the online topology is effectively hardcoded in `appsettings`
(`PipeWire:DefaultCaptureNode`, `PipeWire:OutputSink`), and an in-person run that forgets
to change them captures a sink nothing is feeding and hears nothing at all. The wizard
should own this choice and set the routing from it, rather than it being two settings you
have to remember are coupled.

**Steps it should walk through**, each verified before the next:

1. **Online or in person**, which picks the audio topology above.
2. **Load the audio graph**, and confirm the nodes exist.
3. **Who is on the panel**, and which capture source each name belongs to — pick the name,
   talk into the mic, watch which meter moves, confirm. This is the only reliable way to
   map speakers to channels, and it is the step that produces per-speaker attribution.
4. **Confirm Bubbles' voice reaches where it needs to go** — the virtual mic for online,
   the default output for in person — by measuring it, not by asking whether it sounded right.
5. **Model reachable, named model pulled, and warm.**
6. **TTS answering, reference audio present, and warm.**
7. **Rehearsal echo path off**, and monitoring set to match the venue.
8. **A dry run**: trigger one real answer and watch it the whole way through.

**Each step needs a remedy, not just a verdict.** A step that says "the audio graph is
missing" and offers the button that builds it is worth ten that only report. The current
checks do some of this; the wizard should do it everywhere.

**Lessons already paid for, which the wizard exists to prevent:**

- Device selection and speaker names were held in memory only, so any restart silently
  reset the capture node to the configured default and threw the names away. That is
  almost certainly why speaker attribution "didn't work on the night" — it worked, and
  then something restarted. Now persisted, but the deeper point stands: setup state that
  only exists in a running process is setup state you will lose at the worst moment.
- Bubbles' voice going to a null sink means **hearing nothing is the correct behaviour**,
  and is also the symptom of a completely dead TTS. Ambiguous silence is the enemy; every
  such state needs to be stated out loud by the tool.
- The room-microphone bug would have been caught in seconds by an amplitude check.

**Shape.** The dashboard and the wizard are different jobs over the same checks and the
same actions, so they should share them rather than becoming two codebases. Worth deciding
whether the wizard is a mode of the existing web app or a separate flow that hands over to
it once you are live. Razor Pages is a reasonable alternative to the current plain
HTML/JS, and would be a better fit for a multi-step flow with server-held state.

### 10. Experimentation and test tooling

Called out as being as important as the experiments themselves. Everything so far has been
tested by hand under time pressure, which is how the room-microphone bug survived for hours.

- Replay a recorded panel through the pipeline offline and diff the transcripts.
- A question-set harness: fixed questions, measure time-to-first-audio, reasoning/answer token
  split and word count per model, so "is qwen3.8 better than X" is a measurement rather than a
  vibe. This is also how items 2a, 3 and 8 get decided.
- Capture every response with its timeline attached (`ResponseCapture` already stores text and
  audio — add the timings).
- A real test project. The suppression-gate and chunker tests written during the Linux port
  live in a scratch directory and should be in the repo.

### 11. More display clients, and the virtual camera

**Multiple Bubbles instances already work today.** The orchestrator broadcasts with
`Clients.All`, so a tablet on stage, a screen behind the panel, and a virtual-camera feed can
all connect at once with no code change. Worth testing rather than building.

- **Virtual camera** for online events, and for putting Bubbles on a stage screen or into a
  live stream. `v4l2loopback` is present on the Linux box but not loaded; OBS isn't installed.
  On Wayland/KDE, window capture goes through the xdg-desktop-portal, so OBS is the practical
  route. Twenty minutes when not against a clock.
- **A Blazor Bubbles** would make the display reachable from any screen with a browser — no
  MAUI toolchain, no per-platform build. Given the GTK build had to redraw the whole visual
  anyway because SkiaSharp has no GTK handler, a web version may be the better long-term home.
- **Evaluate [Avalonia.Controls.Maui](https://github.com/AvaloniaUI/Avalonia.Controls.Maui)
  as an alternative to the GTK backend.** Unverified, but the hypothesis is that it can be
  added to the *existing* Bubbles app rather than needing the separate `Bubbles.Gtk` project
  and its hand-drawn scene. If that holds it would collapse two codebases into one and
  potentially bring the real Lottie animations to Linux — which is the GTK build's biggest
  compromise. Worth a spike before investing further in `Bubbles.Gtk`.

---

### 12. Decide what Aspire is still for

Aspire was brought in for two reasons: orchestrating the local setup, and hosting Foundry
Local. Foundry Local was abandoned — it could not find the model, and Aspire would not use
the cached one, which was too many papercuts for the value. And the setup problem is now
being solved by item 9, in the app itself, where the knowledge about what "ready" means
already lives.

So the question is open: **what is Aspire still earning?** The honest answer today is the
dashboard and the OpenTelemetry wiring, which item 1 depends on and item 8a wants on a
second machine. That is a real benefit, but it is not the reason it was adopted.

- Removing it would simplify running the thing on the night to "start the API".
- Keeping it keeps the telemetry story that items 1 and 8a are built on.
- Worth deciding deliberately rather than by drift, and worth doing **after** item 1,
  since that is what will show whether the dashboard is actually being used.

---

## P3 — Nice, later

- **AOT.** Honest assessment: the delays in the logs are overwhelmingly model inference, not
  managed code, so gains will likely be imperceptible. Not harmful, and startup would improve.
  Do it *after* instrumentation says where the time goes — otherwise it's optimising blind.
  Note Whisper.net and other native dependencies may complicate an AOT build.
- **Give Bubbles a body.** 3D-printed beer-mug mascot, small screen behind the mouth driven by
  an ESP32. A Blazor display (item 11) makes this dramatically easier — the ESP32 renders a
  web page instead of needing a native client.

---

## Carried forward, unchanged

- Rolling-summary context strategy stays. Recent verbatim window + last completed summary.
- Moderator button stays as the trigger of record, and as the silent fallback once spoken
  triggers exist.
- VAD is still not the answer to addressing. Endpointing and addressing remain different
  problems.

## What this outing proved

- The two-domain framing held: everything local, nothing read back from the broadcast side.
- Streaming and chunking work; the remaining latency is upstream of the chunker, in
  contention for the model.
- Self-suppression, the silence gate, and the capture path all behaved.
- Model choice matters more than expected. Qwen3.8-27B was a step change over gpt-oss:20b for
  the same pipeline, at the same latency profile — which raises the value of item 10, since
  that finding was luck rather than measurement.
