# The Bubbles Story

Talking points for the panel. Fallback for if the demos fail — but most of this is worth
saying whether they work or not, because the interesting parts are the failures.

**On provenance.** This is reconstructed from the repository and the commit log, not from
memory — deliberately. Asked from the floor what he'd been most excited about while building
Bubbles, Matt's honest answer was that he couldn't remember: it was nine days of manic work
fitted around a job, and what survives of it is the relief that the thing worked at all. The
git history remembers better than any of us do. Everything below is checkable against it, so
it's all defensible if someone pushes back.

---

## If you only land three things

- **It met spec and was still bad.** The original spec allowed ≤10s generation plus ≤3s TTS.
  Bubbles hit that and the room still felt the silence. The metric was wrong, not the build.
- **The AI was the easy part.** The hard parts were audio routing, process lifetimes, and
  platform quirks. The LLM was a config setting.
- **Every serious bug this time round looked healthy in the logs.** Verification was the
  actual skill, not code generation.

---

## The origin

- Started as a joke — "we should invite an AI to the panel" — and then it wasn't a joke.
- Nine days from first commit to going on stage: 13–21 February 2026. 198 files, ~7,600
  lines of C#, 221 lines of Python.
- The README sets the tone deliberately: *"Bubbles is not a product. It’s theatre with
  guardrails."*
- The commit after it first worked end to end is just: **"Working."**
- The photo in the README is captioned *"Figure: It worked!"* — which was genuinely in
  question until it did.

## Constraints that were decided up front, and held

- **No autonomous interjections.** Bubbles speaks only when the moderator triggers it.
- **Must be killable instantly.** There's a cancel and a disable, and a scripted exit line:
  *"Looks like we've overfilled it. Bubbles, thanks for joining us tonight."*
- **Failure is an acceptable outcome.** Written into the design principles from day one.
- **≤150 words, spoken aloud.** Self-deprecating humour only, never at anyone's expense,
  never attacks individuals.
- The state machine is four states — Idle, Listening, Thinking, Speaking — plus an
  "overfilled" escape hatch. The whole UI is a beer glass that can be dramatically overfilled
  to shut it up. The kill switch is part of the bit.

## What broke the first time

- **The latency problem wasn't where the spec looked.** Spec: response generation ≤10s,
  TTS ≤3s. Both were met. It still read as broken.
- The real metric was **time-to-first-audio** — the gap between the trigger and *any* sound.
  Total generation time was never what the room felt.
- **The filler phrases made it worse.** Canned "Great question!" clips were added to cover
  the gap. The effect was "Great question!" … then ten seconds of silence. The filler
  promised an answer that didn't arrive, which is worse than saying nothing.
- Lesson: a hack that covers a symptom can amplify it. Those clips have now been deleted,
  not kept as belt-and-braces — they'd fight the streamed opening.

## The fix, and why it's the interesting engineering

- Stream the LLM's tokens and flush to TTS at **clause boundaries**, so it starts speaking
  while it's still thinking.
- **Never chunk on token count** — a fixed split lands mid-phrase and the TTS renders
  audibly bad prosody at the seam. Punctuation only.
- **First chunk short, later chunks longer.** The opening clause sets time-to-first-audio;
  after that, playback of the previous chunk is buying time.
- Too short is also wrong — a two-word opening comes out prosodically flat. A short opening
  *clause* is the sweet spot.
- Measured on the rehearsal rig: **521ms to first audio, versus 2,027ms** unstreamed. The
  honest number is the ~120ms of that which is our own pipeline; the rest is the model.
- Chunk N+1 is synthesised while chunk N is still playing, through one continuous playback
  stream, so there's no gap at the seams.
- Reasoning models nearly break this: if you stream the *thinking* tokens, first-audio is
  gated on the entire reasoning phase — worse than not streaming at all. The answer and
  reasoning channels are separated so only the answer reaches the speaker.

## Things that were deliberately *not* built

Good material, because the discipline is in the "no"s.

- **A StreamYard bot.** The instinct was a Teams-style bot API. There isn't one — no SDK,
  no webhook for live call audio. That absence is *why* the whole design routes through a
  virtual sink and a guest slot.
- **Reading captions off Twitch.** No clean real-time transcript API, and stream latency of
  3–8 seconds is fatal for conversation.
- **Pulling the audio stream from Twitch.** Same latency, just at a different layer.
- This produced the framing that everything else hangs off: **two audio domains.** The local
  one — mics, capture, LLM, TTS, virtual mic — is sub-second and safe to build on. The
  broadcast one — StreamYard, CDN, viewers — is output only. Every rejected idea was a
  version of accidentally reading from the broadcast domain.
- **Keyword matching as autonomy.** Rejected: brittle against real address, misses "I'd love
  the AI's take", false-fires on "I asked Bubbles yesterday".
- **VAD as the autonomy mechanism.** Rejected as insufficient: VAD answers "has someone
  stopped speaking", not "should the AI respond". Alone it fires after every sentence.
- The distinction that matters: **endpointing and addressing are different problems.** Most
  "make it autonomous" suggestions conflate them.

## War stories

- **Foundry Local.** Commit message, verbatim: *"Got gpt-oss:20b working with Ollama. Tried
  countless things with Foundry Local, lots of problems."* The code comments are still in
  the repo explaining what was tried and why it was abandoned — including that it wouldn't
  use the cached model anyway. Sometimes the right answer is a comment saying "don't".
- **CUDA on Windows.** PyTorch CUDA wouldn't work natively, so the TTS server ran inside WSL
  and the app shelled out to it. That entire workaround evaporated on Linux.
- **Nahimic — the one that actually torpedoed the first night.** Worth telling in full,
  because nothing in it is a coding problem:
  - The laptop was on the floor near the panelists. A space and logistics compromise rather
    than a decision, but planning would have caught it.
  - Bubbles worked, then simply stopped responding. Windows power settings had put the screen
    to sleep, so there was no way to see what was happening without walking over.
  - What was happening: **the laptop was rebooting.** Nahimic — Windows audio "enhancement"
    middleware bundled with the machine — was causing spontaneous restarts. Vendor software
    nobody chose, sitting between the app and the sound hardware, in the one part of the stack
    with no fallback.
  - The tell was a roughly two-minute window between the UEFI screens and the login screen
    dimming. Miss that window and there is no evidence at all. An attendee actually saw it and
    tried to flag it, but Matt was heads-down on the panelists and the AV and missed the
    signal too.
  - Sneaking over to find a Windows login screen, logging in, restarting everything, and
    having Bubbles come straight back is a genuinely disorienting debugging experience.
  - It happened at least twice. The second time it wasn't restarted.
  - **The wrong conclusion at the time:** that the laptop was being asked to do too much, and
    the reboots tracked GPU load. It's a high-spec machine bought for exactly this kind of
    work, and it hadn't been owned long enough for the pattern to be obvious. The reboots
    turned out to be effectively random. That investigation is a story of its own — Beer
    Driven Devs episode 68 — and doesn't need rehashing here.
  - Gone entirely on Linux, and the single biggest reliability win of the port.
- **The real lesson from that night isn't Nahimic, it's the absence of a feedback loop.** The
  machine was face-down on the floor with its screen asleep, the operator was busy operating,
  and an audience member had better telemetry than the person running the system.
- **Dev tunnel.** Audio device enumeration silently broke over the tunnel and had to be
  fixed separately — the classic "works locally" failure.
- **Privacy.** There's a commit ignoring `.wav` and `.pt` files, because the TTS clones a
  voice from a real sample. Voice cloning has an artefact-hygiene problem most people don't
  think about until it's in git.
- **The typo commit.** *"Enab,e dynamica animation states"*. Ships at 1am.

## The Linux port, six months later

- The repo sat untouched from 21 February to 16 August. Picking it back up is its own story:
  the code was fine, the *environment* had rotted.
- Windows-only assumptions were load-bearing in three places — capture, playback, and TTS —
  not one.
- NAudio is Windows-only. Replaced by capturing directly from PipeWire nodes, which turned
  out to be a better mental model anyway: the routing is a real audio graph you can inspect,
  not a virtual cable you have to believe in.
- The Windows path still works. New backend in front, proven thing behind it, hard toggle
  between them.

## Bugs from this week that are worth telling

All four looked completely healthy from the outside. This is the theme.

- **The capture bug.** `pw-cat --target <sink>.monitor` doesn't resolve — that's a PulseAudio
  compatibility name, not a PipeWire node. It doesn't error; it silently falls back to the
  default source. So it transcribed a **room microphone** while every log line looked
  correct. Found by measuring amplitude instead of byte count — bytes arrive whatever you're
  attached to.
- **Whisper hallucinating.** Given silence, Whisper confidently produces "Thank you.",
  "[BLANK_AUDIO]", and fragments of its own prompt. During a panel there are long silences,
  so the rolling summary was filling with dialogue nobody said. Fixed with an RMS gate.
- **The bubbles that weren't there.** The particle simulation ran in pixels, but the height
  is only known at draw time — so every bubble sat hidden under the foam. On a real device
  it would have *mostly* worked, which is exactly the kind of bug that survives to showtime.
- **Doubled audio.** Re-running the setup script stacked a second identical loopback. Two
  unaligned copies of the same signal — louder, and comb-filtered.

## The bit that's actually about developer careers

- **Parts of this were built by AI agents.** Two of the merged pull requests came off
  `copilot/` branches: the core pipeline, and the device management page.
- **And the code review caught real problems** — there are commits fixing AI-authored code
  for a `.Result` deadlock risk and for dependency-injection misuse. The generation was
  fine. The judgement about what was wrong with it was the human part.
- An AI panelist substantially built by AI assistants, being asked on stage about the future
  of developers, is a joke that writes itself — but the honest version is more interesting:
  **the assistants were good at the code and useless at the problem.**
- Nothing that nearly sank this was a coding problem. It was: which audio node is this
  actually attached to; does this process die when its parent does; is that silence real or
  is my microphone muted; is the thing I'm measuring the thing I think I'm measuring.
- **The skill that mattered was verification.** Every serious bug this week passed "does it
  run", "does it build", "do the logs look right". They only fell to a test that measured the
  thing itself.
- If you want a one-liner: *the job is moving from writing the code to knowing what would
  convince you it works.*

## The second outing

- Online this time: StreamYard to Twitch, with Bubbles joining as its own guest — a virtual
  microphone fed by the TTS, and a browser tab as its ears.
- **It worked.** Bubbles spoke to the audience and heard the other hosts. Nothing rebooted.
- Qwen3.8-27B in place of gpt-oss:20b was a step change in how creative and funny the answers
  were, on an identical pipeline. Six months is a long time in local models — and that finding
  was luck rather than measurement, which is its own lesson.
- **The best failure of the night.** An audience member asked Bubbles to say "To be or not to
  be, that is the question" with "Alt.NET" after every word. It didn't refuse — it went
  completely silent, twice. Leading theory: it's a reasoning model, the word-by-word
  transformation burned the entire output budget on thinking, and the answer came back empty.
  Zero text reached the speech pipeline, which happily did nothing at all.
- The lesson generalises past this bug: **the pipeline had no floor.** Every component behaved
  correctly and the result was silence on stage. "Say something, anything" needs to be an
  explicit guarantee, not an emergent property.
- Latency is still the enemy. Streaming fixed the shape of it — Bubbles starts talking quickly
  — but time-to-first-response is still too long, and the cause is contention for a single GPU
  between summarisation and answering. Not a code problem. A topology problem.

## The thing that makes all of this possible

- The README calls Bubbles a gimmick on purpose. *"Not a product. Theatre with guardrails."*
- That isn't modesty, it's **a design input**. Because failure is acceptable, the whole thing
  can be hacky and fast and built in nine days around a job. If it breaks on stage, everyone
  laughs and the panel carries on. That tolerance is what buys the speed.
- The honest flip side: **this is not a robust, reliable, tested system.** It has no test
  project, no operator alerting, no guaranteed floor when the model returns nothing, and a
  setup procedure that runs on memory and attention.
- None of that is a criticism of the build — it's the correct engineering for the stated
  quality bar. The interesting bit is that **the bar is set by context, not by code.** The
  same repo submitted to a serious conference, or used anywhere someone depends on it, is
  suddenly held to a standard it doesn't meet, without a line of it having changed.
- Useful thing to say to a room of developers: most of us have shipped something whose real
  spec was "it's fine if this breaks", and most of us have watched that assumption quietly
  expire without anyone noticing. Knowing which kind of system you're holding is the skill.

## Closers

- Bubbles is a member of the panel that cannot be offended, cannot be interrupted, and can be
  switched off mid-sentence. Choose your own moral.
- It's theatre with guardrails — and the guardrails are the engineering.
- It took two outings and a full rewrite of the audio stack to make it sound like it answers
  quickly. It doesn't think faster than it did in February. It just starts talking sooner.
- Both nights it worked, and both nights the interesting part was what didn't. That's the
  actual deliverable.
- And the standing principle, unchanged since day one: if it fails, we thank it for its
  service and move on.
