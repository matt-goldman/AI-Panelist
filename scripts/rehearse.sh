#!/usr/bin/env bash
#
# Run the panelist in rehearsal mode: real audio path, no heavyweight dependencies.
#
#   STT      Whisper          (real - downloads ~148MB on first run)
#   LLM      Mock             (canned response text, no Ollama needed)
#   TTS      espeak-ng        (real words, no GPU, no model warmup)
#   Audio    PipeWire         (real - into bubbles-tts-sink, out of bubbles-tab-sink.monitor)
#
# This is enough to exercise capture -> STT -> response -> TTS -> virtual mic and, with
# `bubbles-audio.sh echo on`, to prove the self-suppression gate works. Swap TTS to
# Qwen3-TTS and LLM to Ollama for the real thing.
#
# Usage:
#   ./rehearse.sh            Start it
#   ./rehearse.sh trigger    Ask Bubbles to respond (run from a second terminal)

set -euo pipefail

PORT="${BUBBLES_PORT:-5199}"
BASE="http://127.0.0.1:$PORT"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ "${1:-}" == "trigger" ]]; then
    echo "Triggering a response..."
    curl -fsS -X POST "$BASE/api/panelist/trigger" && echo "  ok" || echo "  failed - is rehearse.sh running?"
    exit 0
fi

die() { echo "error: $*" >&2; exit 1; }

command -v espeak-ng >/dev/null || die "espeak-ng not installed. Install it, or set TTS to Qwen3-TTS."
pactl list short sinks | awk '{print $2}' | grep -qx bubbles-tts-sink \
    || die "Audio graph is not up. Run ./bubbles-audio.sh up first."

echo "Rehearsal configuration:"
echo "  STT   Whisper (real)"
echo "  LLM   Mock (canned text - no Ollama required)"
echo "  TTS   espeak-ng (robotic, but real words Whisper can transcribe)"
echo "  Audio PipeWire"
echo
"$REPO_ROOT/scripts/bubbles-audio.sh" status | sed -n '/Bubbles graph:/,/^$/p'
echo "Listening on $BASE"
echo "Trigger a response from another terminal with:  ./scripts/rehearse.sh trigger"
echo
echo "First run downloads the Whisper model (~148MB) before transcription starts."
echo

cd "$REPO_ROOT/src/API"

# Environment variables win over appsettings.Development.json, so the Ollama/Qwen entries
# there are overridden without editing any files.
exec env \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$BASE" \
    AIPanelist__SttServiceType=Whisper \
    AIPanelist__LlmServiceType=Mock \
    AIPanelist__TtsServiceType=espeak \
    AIPanelist__AudioDeviceServiceType=PipeWire \
    AIPanelist__AudioPlaybackServiceType=PipeWire \
    AIPanelist__EnableFillerPhrases=false \
    PipeWire__DefaultCaptureNode=bubbles-tab-sink.monitor \
    PipeWire__OutputSink=bubbles-tts-sink \
    dotnet run --no-launch-profile
