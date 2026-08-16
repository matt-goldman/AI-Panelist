# AI Panelist — Meetup Backlog

Prioritised work for the online (StreamYard → Twitch) meetup next month. Second outing for the AI panelist ("Bubbles"). Everything below is scoped around one real engineering push (streaming) plus a Linux port; the rest is config or optional.

**Principle throughout:** new/autonomous approach in front, proven thing behind it, hard toggle between. Ship the reliable version; treat everything autonomous as "if there's time, no shame if not."

---

## P0 — Must work or nothing does

### 1. Port audio capture off NAudio
- NAudio is Windows-only (WASAPI/WinMM); won't run on Linux.
- Replace with a cross-platform capture: PortAudio binding (e.g. PortAudioSharp) or capture directly from a PipeWire/PulseAudio node.
- Prefer capturing from a PipeWire node to keep everything in one mental model (see #2).
- This is a small rewrite, not a config change.

### 2. Rebuild audio routing on PipeWire
- Replaces the Windows virtual-cable dance (VoiceMeeter/BlackHole).
- **Capture side:** route the StreamYard browser tab's audio into a dedicated virtual sink, capture from there. Remote hosts only exist in this mix — the only local mic is mine, so mic-node capture won't work.
- **Output side:** TTS output → a virtual source → selected as the AI guest's mic in StreamYard.
- Wire with `pw-link` / `qpwgraph`.
- **Validate early — most likely to surprise:** confirm a virtual PipeWire source shows up selectable in StreamYard's browser device picker. Test before building on it.
- Shake this out early in prep, not the week of — most likely to eat an evening.

### 3. AI joins as its own StreamYard guest
- Second guest slot: camera = avatar/static card, mic = the TTS virtual source from #2.
- Depends on #2 being validated.

### 4. Self-suppression gate (avoid echo/self-trigger loop)
- AI's TTS goes into StreamYard and comes back in the captured tab mix → would be transcribed and could loop.
- Pause/discard STT while the AI is speaking (app owns TTS state, so it knows exactly when).
- **Gate at the STT boundary, not the mic:** keep audio capture running continuously, drop/ignore the *transcript* for the window. Don't stop/start the capture node (buffer glitches).
- **Tail buffer:** keep STT suppressed ~200–500ms after TTS playback ends, to cover audio still flushing through StreamYard → CDN → tab capture. Prevents the AI's own trailing speech leaking into the next window.

---

## P1 — The thing the audience actually feels

### 5. Chunk the TTS / stream the response
- **This is the core fix.** January's real problem was latency *after* the trigger, not the trigger. Metric that matters shifts from total generation time to **time-to-first-audio**.
- Stream LLM tokens; flush to TTS on **sentence/clause boundaries** (`.`, `?`, `!`, or strong breaks `,`/`;`/`—` past a minimum length). Do **not** chunk on fixed token count — splits mid-phrase, Qwen3 renders bad prosody at seams.
- **First chunk short** (fast time-to-first-audio) but not so short it comes out prosodically flat — short opening clause is the sweet spot. Later chunks can be longer.
- **Queue for gapless playback:** generate/queue chunk N+1 before N finishes playing. Generation-to-speech ratio was already well under realtime, so this holds comfortably after the first chunk.
- **Drop the canned "Great question!" filler phrases entirely** — the first real clause now fills the gap coherently. The old fillers will fight the streamed opening (that "Great question!… [10s silence]" effect was the filler promising an answer that didn't come).

---

## P2 — Low-risk config, real quality win

### 6. Swap local LLM → frontier model, thinking on
- Local was chosen for latency; chunking removes that reason. Frontier time-to-first-token is often faster than local-on-consumer-GPU, and the network round-trip hides inside the first chunk.
- LLM is already swappable via config (built cloud-first originally) — this part is done.
- **Keep thinking on but bounded.** Quality delta with reasoning is exactly what makes the AI panelist worth having on *this* topic (developer careers in the age of AI).
- **Critical:** chunk on the **answer** stream, not the thinking stream. If reasoning tokens stream first, time-to-first-audio is gated on the whole thinking phase = naked dead air. Use separate reasoning/answer channels if the API exposes them; feed answer → TTS, discard or surface reasoning as an avatar "thinking" state.
- **Model selection matters:** weight fast first-token + streamable reasoning as heavily as raw benchmark quality. Time a couple on the actual question set, don't go by reputation.
- Keep local model wired as a fallback (network dependency now on the critical path of a live event). Cost is trivial — don't factor it in.

---

## P3 — Optional autonomy (only if P0–P2 are solid)

**Primary trigger remains the moderator app (proven, human = perfect intent authority). Everything here replaces the part that worked, to remove a button press. No shame in shipping without any of it.**

### 7. Spoken trigger phrases (replace/augment the moderator button)
- I stay the intent authority — I just issue the trigger by *saying* a known phrase instead of pressing a button. Redirect messy host address ("our resident AI overlord…") into a clean trigger ("Yeah, what do you think, Bubbles?").
- Spoken trigger doubles as the **endpoint signal for free** — end of my phrase = the turn boundary. Removes the need for VAD timing in this design.
- **Dictionary design — match on trigger *cores*, not whole sentences.** Anchor every core on **"Bubbles" + a direction word** ("you"/"your"/"take"/"weigh in"/"view") so third-person mentions don't false-fire and normal host-to-host chatter ("what do you think") doesn't match.
  - Sample cores: "what do you think, Bubbles" / "over to you, Bubbles" / "your take, Bubbles" / "how about you, Bubbles" / "care to weigh in, Bubbles" / "what would you say, Bubbles" / "give us your view, Bubbles".
  - Add one non-question / initiative core: "Bubbles, jump in here" / "take it away, Bubbles".
  - Keep 5–8 total; ~2–3 as habitual go-tos for recall under pressure, the rest for audible variety across the hour.
- **Keep the button as silent fallback** (STT drops the phrase / I fluff the wording). Only I know it exists.
- "Bubbles" transcribes fine — no casing/fuzzy needed on the name unless testing says otherwise.

### 8. Rolling STT stream evaluation (matcher for #7)
- **Concurrency shape:** runs as a **continuous parallel process** scanning the gated STT stream and raising a trigger *event* on match — **not** a step in the response pipeline. It's always watching, independently of whether the AI is mid-response. This is *why* the spoken trigger doubles as the endpoint signal (#7): the matcher is already scanning, so the instant my phrase completes it fires — no separate endpointing step needed.
- **Sliding buffer** of last few seconds of transcript (phrases are short). Append each segment, drop old text. Handles phrases straddling segment boundaries.
- On each new segment, normalise (lowercase, strip punctuation) buffer + cores the same way, substring-match each core over the tail.
- **Consumed-marker on match** so one utterance can't fire multiple times as the window slides (clear buffer or mark up-to-match consumed).
- **Gotchas:**
  - **Match on *finalised* segments only** (or debounce across a revision). Streaming Whisper revises interim hypotheses ("sink" → "think") — biggest source of flaky/duplicate fires.
  - Matcher must read the **gated** stream (same self-suppression as #4), not a raw pre-gate tap.
  - **Exact substring first**; only add fuzzy (Levenshtein on core) if testing shows Whisper mangling the specific phrases in my voice. Fuzzy raises false-positive risk — measure before adding.

### 9. VAD (endpoint detection) — lowest priority, likely v3
- VAD answers "has someone stopped speaking?" (endpointing) — **not** "should the AI respond?" (that's intent/addressing). They're orthogonal; VAD alone can't do addressing and would fire after *every* host sentence = worse than the button.
- With the moderator button *or* spoken triggers (#7), intent + endpointing are already collapsed into one act, so VAD isn't needed for next month.
- If added: Silero VAD (light, CPU, sits next to Whisper off the GPU budget). Tune silence-hangover **generously** — remote audio over StreamYard has non-turn-end pauses/jitter.
- Only runs on inbound host audio during the AI's silent windows (self-suppression makes AI-voice false-triggers a non-issue by construction).
- Nice-to-have use even with the button: trigger *arms* the response, VAD *fires* it the instant the current speaker stops (snappier). Polish, not plan.

---

## Left as-is for next month (don't rebuild now)

- **Rolling-summary context strategy** (~30s summary refresh + full recent window) is sound. Bounded token cost, whole-conversation awareness.
  - One check when moving to streaming/frontier: ensure a response request can't race a summary update — read a **consistent context snapshot**, not a half-updated summary. Trivial to get right, annoying to debug live.
  - Frontier's larger context *could* let you carry more raw transcript and lean less on summarisation — "could," not "should," this close to the event. File with v3.

## Fixed for free by the Linux move
- No more Nahimic reboots.
- Qwen3 TTS runs natively (was WSL-only on Windows) — removes that awkwardness.

## Deferred to "v3" (proper autonomous version, built + tested outside live pressure)
- Full autonomous addressing detection via LLM tool call (`should_respond`) seeing the ongoing transcript — more robust to varied natural address than keyword matching, but requires an **always-on** architecture (continuous STT on all host audio, LLM invoked far more often to *judge* than to *answer*). Meaningfully different from the current trigger-gated design. Not a next-month tweak.

---

## Decisions & discarded options

Rationale for the roads not taken — recorded so they don't get re-litigated.

**Core framing — two audio domains, stay in the local one:**
- **Local domain (build on this):** panel/host audio → capture → Whisper.NET → LLM → Qwen3 TTS → virtual sink → StreamYard guest mic. Sub-second, all inside the PipeWire graph.
- **Broadcast domain (never read from this):** StreamYard → Twitch → CDN → viewers. Output only. The AI's voice lands here because it went *into* StreamYard as a guest, not because anything was fed back from Twitch.
- Every rejected option below is a version of accidentally reading from the broadcast domain, or of giving up human intent control.

**Discarded:**
- **StreamYard bot/participant API** (the Teams-bot instinct) — no bot/participant API, no SDK/webhook for live call audio. This is *why* the whole design routes through a virtual sink + guest slot instead.
- **Twitch STT/captioning as the STT source** — no clean public real-time transcript API (captioning is third-party/extension/OBS-plugin territory); and stream latency (~3–8s) is fatal for conversational response. Would also have been fragile scraping captions off our own player.
- **Grabbing the audio *stream* from Twitch** (instead of the local tab) — same latency, just at a different layer: post-encode, post-ingest, post-CDN. Disqualifying regardless of capture method (HLS/streamlink/yt-dlp or tab). This established the two-domains framing above.
- **Direct mic-node capture** — viable in January (everyone in-room, mics into one PC) but **not this event**: only Matt's mic is local; the two hosts are remote and exist only in the StreamYard tab mix. Hence capture from the tab sink (item 2), not mic nodes.
- **Keyword matching as *autonomous intent detection*** — rejected: brittle against real natural address (misses "I'd love the AI's take"; false-fires on third-person "I asked Bubbles yesterday"). The *accepted* design (item 7) is keyword-as-spoken-command with **Matt still the intent authority** — a different thing entirely. He converts messy host address into a known trigger himself.
- **Canned "thinking" filler phrases** (January's latency cover) — dropped: the gap *after* the filler is what read as broken ("Great question!" → 10s silence). Streaming/chunking (item 5) makes the first real clause the gap-filler instead, so the hack is deleted, not kept as belt-and-braces (it would fight the streamed opening).
- **VAD as the autonomy mechanism** — rejected as insufficient: VAD does endpointing, not addressing; alone it fires after every host sentence = worse than the button. Only useful *alongside* an intent signal (item 9).
- **Turning off LLM thinking to cut latency** — rejected as the *primary* lever: it's the cheap fix, but streaming+chunking is the better one and preserves answer quality. Keep thinking on but bounded (item 6). Disabling thinking and streaming attack different parts of latency and can stack, but killing thinking outright sacrifices the quality that justifies the AI panelist.
