#!/usr/bin/env bash
#
# Bubbles audio graph for the StreamYard event (Linux / PipeWire).
#
# Replaces the Windows virtual-cable dance (VoiceMeeter / VB-Cable). Builds two null
# sinks and a remapped source:
#
#   StreamYard browser tab ──> bubbles-tab-sink ──┬── .monitor ──> Whisper capture
#                                                 └── loopback ──> your headphones
#
#   Bubbles TTS ──> bubbles-tts-sink ── .monitor ──> "Bubbles Mic" ──> StreamYard guest mic
#
# Capture side: remote hosts only exist inside the StreamYard tab mix, so we capture the
# tab's own output rather than any mic node. The loopback exists so you can still hear the
# hosts while their audio is being diverted into the null sink.
#
# Output side: a remapped source (not a raw monitor) so the browser device picker shows it
# as a normal microphone called "Bubbles Mic".
#
# Usage:
#   ./bubbles-audio.sh up            Create the graph (idempotent)
#   ./bubbles-audio.sh down          Tear it down
#   ./bubbles-audio.sh status        Show what exists right now
#   ./bubbles-audio.sh route         Move a browser tab's audio into bubbles-tab-sink
#   ./bubbles-audio.sh play FILE     Play a file into bubbles-tab-sink (fake remote hosts)
#   ./bubbles-audio.sh echo on|off   Feed Bubbles' own voice back into the tab mix,
#                                    simulating the StreamYard return path (see below)
#
# Env:
#   MONITOR_SINK   Sink you listen on. Defaults to the current default sink,
#                  resolved before anything is created.
#   NO_LOOPBACK=1  Skip the "hear the hosts" loopback (e.g. if you monitor in StreamYard).
#   ECHO_DELAY_MS  Return-path delay for `echo on` (default 300).

set -euo pipefail

TAB_SINK="bubbles-tab-sink"
TTS_SINK="bubbles-tts-sink"
VIRTUAL_MIC="bubbles-mic"
STATE_FILE="${XDG_RUNTIME_DIR:-/tmp}/bubbles-audio.modules"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "  $*"; }

require_tools() {
    for tool in pactl pw-cat; do
        command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found. Install pipewire-pulse and pipewire-audio (pipewire-utils)."
    done
    pactl info >/dev/null 2>&1 || die "Cannot talk to the audio server. Is PipeWire running?"
}

sink_exists() { pactl list short sinks | awk '{print $2}' | grep -qx "$1"; }
source_exists() { pactl list short sources | awk '{print $2}' | grep -qx "$1"; }
sink_index() { pactl list short sinks | awk -v n="$1" '$2 == n { print $1; exit }'; }

remember_module() { echo "$1" >> "$STATE_FILE"; }

# Does a module-loopback already exist for this source -> sink pair?
# Without this, re-running `up` stacks duplicate loopbacks, which doubles the audio
# and adds a comb-filtered flam because the copies are not sample-aligned.
loopback_exists() {
    pactl list short modules \
        | grep -q "module-loopback.*source=$1[[:space:]].*sink=$2[[:space:]]" \
        || pactl list short modules | grep "module-loopback" | grep -q "source=$1 .*sink=$2 "
}

# List playback streams, optionally filtered to one sink by name.
# Properties are accumulated per record and emitted when the record ends, otherwise
# fields bleed between streams and rows get attributed to the wrong application.
list_sink_inputs() {
    local filter_sink="${1:-}"
    local want_index=""
    [[ -n "$filter_sink" ]] && want_index="$(sink_index "$filter_sink")"

    pactl list sink-inputs | awk -v want="$want_index" '
        function emit() {
            if (id == "") return
            if (want != "" && sink != want) return
            printf "  [%-5s] sink=%-6s %-12s %s\n", id, sink, (app == "" ? "(none)" : app), media
        }
        /^Sink Input #/ { emit(); id = substr($3, 2); sink = ""; app = ""; media = "" }
        /^[ \t]*Sink: / { sink = $2 }
        /application\.name = / { app = $0; sub(/.*= "/, "", app); sub(/"$/, "", app) }
        /media\.name = / { if (media == "") { media = $0; sub(/.*= "/, "", media); sub(/"$/, "", media) } }
        END { emit() }
    '
}

cmd_up() {
    require_tools

    # Resolve the listening sink BEFORE creating null sinks, otherwise a re-run could
    # pick up bubbles-tab-sink as the default and build a feedback loop.
    local monitor_sink="${MONITOR_SINK:-$(pactl get-default-sink)}"
    if [[ "$monitor_sink" == "$TAB_SINK" || "$monitor_sink" == "$TTS_SINK" ]]; then
        die "Default sink is '$monitor_sink', which is one of our null sinks. Set MONITOR_SINK to your real output device."
    fi

    echo "Building Bubbles audio graph..."

    if sink_exists "$TAB_SINK"; then
        info "$TAB_SINK already exists, leaving it alone"
    else
        local id
        id=$(pactl load-module module-null-sink \
            sink_name="$TAB_SINK" \
            channel_map=stereo \
            'sink_properties=device.description="Bubbles\ Tab\ Capture"')
        remember_module "$id"
        info "created sink $TAB_SINK (module $id)"
    fi

    if sink_exists "$TTS_SINK"; then
        info "$TTS_SINK already exists, leaving it alone"
    else
        local id
        id=$(pactl load-module module-null-sink \
            sink_name="$TTS_SINK" \
            channel_map=stereo \
            'sink_properties=device.description="Bubbles\ TTS\ Output"')
        remember_module "$id"
        info "created sink $TTS_SINK (module $id)"
    fi

    if source_exists "$VIRTUAL_MIC"; then
        info "$VIRTUAL_MIC already exists, leaving it alone"
    else
        local id
        id=$(pactl load-module module-remap-source \
            source_name="$VIRTUAL_MIC" \
            master="$TTS_SINK.monitor" \
            channel_map=stereo \
            'source_properties=device.description="Bubbles\ Mic"')
        remember_module "$id"
        info "created virtual mic $VIRTUAL_MIC (module $id)"
    fi

    if [[ "${NO_LOOPBACK:-0}" == "1" ]]; then
        # Actively remove any existing host monitor loopback, not just skip creating one.
        # Otherwise NO_LOOPBACK silently does nothing after a previous plain `up`, and you
        # think you are testing silently when you are not.
        local removed=0
        while read -r id; do
            [[ -z "$id" ]] && continue
            pactl unload-module "$id" 2>/dev/null && removed=$((removed + 1)) || true
        done < <(pactl list short modules \
                    | grep "module-loopback" \
                    | grep "source=$TAB_SINK.monitor" \
                    | grep -v "sink=$TAB_SINK" \
                    | awk '{print $1}')

        if [[ "$removed" -gt 0 ]]; then
            info "NO_LOOPBACK=1: removed $removed existing host monitor loopback(s) — you will NOT hear the hosts"
        else
            info "skipping host monitor loopback (NO_LOOPBACK=1) — you will NOT hear the hosts"
        fi
    elif loopback_exists "$TAB_SINK.monitor" "$monitor_sink"; then
        info "host monitor loopback already exists, leaving it alone"
    else
        # Without this the hosts' audio vanishes into the null sink and you hear nothing.
        local id
        id=$(pactl load-module module-loopback \
            source="$TAB_SINK.monitor" \
            sink="$monitor_sink" \
            latency_msec=40 \
            'sink_input_properties=media.name="Bubbles\ host\ monitor"')
        remember_module "$id"
        info "looped $TAB_SINK.monitor -> $monitor_sink so you can hear the hosts (module $id)"
    fi

    echo
    echo "Done. Next:"
    echo "  1. Point the StreamYard tab's audio at '$TAB_SINK'  ->  ./bubbles-audio.sh route"
    echo "  2. In the AI guest's StreamYard tab, pick 'Bubbles Mic' as the microphone."
    echo "  3. Check appsettings has:"
    echo "       PipeWire:DefaultCaptureNode = $TAB_SINK.monitor"
    echo "       PipeWire:OutputSink         = $TTS_SINK"
}

cmd_down() {
    require_tools
    echo "Tearing down Bubbles audio graph..."

    if [[ -f "$STATE_FILE" ]]; then
        # Unload in reverse order so the remap source goes before the sink it depends on.
        tac "$STATE_FILE" | while read -r id; do
            [[ -z "$id" ]] && continue
            if pactl unload-module "$id" 2>/dev/null; then
                info "unloaded module $id"
            fi
        done
        rm -f "$STATE_FILE"
    else
        info "no state file; falling back to unloading by name"
    fi

    # Belt and braces: catch anything left from a previous run whose state file was lost.
    pactl list short modules \
        | grep -E "$TAB_SINK|$TTS_SINK|$VIRTUAL_MIC" \
        | awk '{print $1}' \
        | while read -r id; do
            pactl unload-module "$id" 2>/dev/null && info "unloaded stray module $id" || true
        done

    echo "Done."
}

cmd_status() {
    require_tools

    echo "Bubbles graph:"
    for node in "$TAB_SINK" "$TTS_SINK"; do
        if sink_exists "$node"; then
            echo "  [ok]      sink   $node (index $(sink_index "$node"))"
        else
            echo "  [MISSING] sink   $node"
        fi
    done
    source_exists "$VIRTUAL_MIC" \
        && echo "  [ok]      source $VIRTUAL_MIC" \
        || echo "  [MISSING] source $VIRTUAL_MIC"

    local loopbacks
    loopbacks=$(pactl list short modules | grep -c "module-loopback.*source=$TAB_SINK.monitor" || true)
    if [[ "$loopbacks" -gt 1 ]]; then
        echo "  [WARN]    $loopbacks host monitor loopbacks are loaded — audio will be doubled."
        echo "            Run './bubbles-audio.sh down' then 'up' to reset."
    fi

    local monitors
    monitors=$(pactl list short modules \
                 | grep "module-loopback" | grep "source=$TAB_SINK.monitor" \
                 | grep -vc "sink=$TAB_SINK" || true)
    if [[ "$monitors" -gt 0 ]]; then
        echo "  [audible] host monitor loopback is ON — test audio comes out of your speakers"
    else
        echo "  [silent]  host monitor loopback is OFF — you will NOT hear the hosts during the event"
    fi

    local echoes
    echoes=$(pactl list short modules | grep -c "module-loopback.*source=$VIRTUAL_MIC .*sink=$TAB_SINK" || true)
    [[ "$echoes" -gt 0 ]] && echo "  [rehearsal] echo return path is ON — Bubbles hears itself (see 'echo off')"

    echo
    echo "Streams actually routed INTO $TAB_SINK (this is what Whisper will hear):"
    local routed
    routed=$(list_sink_inputs "$TAB_SINK")
    if [[ -z "$routed" ]]; then
        echo "  (none — nothing is feeding the capture sink; run './bubbles-audio.sh route')"
    else
        echo "$routed"
    fi

    echo
    echo "All playback streams, for reference:"
    list_sink_inputs
}

cmd_route() {
    require_tools
    sink_exists "$TAB_SINK" || die "$TAB_SINK does not exist. Run './bubbles-audio.sh up' first."

    echo "Playback streams (sink= is the sink index they currently play into):"
    echo
    list_sink_inputs
    echo
    echo "$TAB_SINK is sink index $(sink_index "$TAB_SINK")."
    read -rp "Sink input ID to move into $TAB_SINK: " input_id
    [[ -n "$input_id" ]] || die "no ID given"

    pactl move-sink-input "$input_id" "$TAB_SINK"
    echo "Moved sink input $input_id -> $TAB_SINK"
    echo
    echo "Verifying:"
    list_sink_inputs "$TAB_SINK"
    echo
    echo "Tip: browsers sometimes create a fresh sink input when audio restarts, which"
    echo "     lands back on the default sink. Re-check with './bubbles-audio.sh status'."
}

# Play a file into the capture sink. With a recording of a panel discussion this
# stands in for the remote hosts without needing a conferencing service at all.
cmd_play() {
    require_tools
    local file="${1:-}"
    [[ -n "$file" ]] || die "usage: $0 play <audio-file>"
    [[ -f "$file" ]] || die "no such file: $file"
    sink_exists "$TAB_SINK" || die "$TAB_SINK does not exist. Run './bubbles-audio.sh up' first."

    echo "Playing '$file' into $TAB_SINK (Ctrl-C to stop)..."
    pw-cat --playback --target "$TAB_SINK" "$file"
}

# Rehearsal only: wire Bubbles' own TTS output back into the capture sink, so the app
# hears itself the way it will once its voice comes back through StreamYard's mix.
# This is what makes the self-suppression gate testable without a live stream.
cmd_echo() {
    require_tools
    local mode="${1:-}"
    local delay="${ECHO_DELAY_MS:-300}"

    case "$mode" in
        on)
            sink_exists "$TAB_SINK" || die "$TAB_SINK does not exist. Run './bubbles-audio.sh up' first."
            if loopback_exists "$VIRTUAL_MIC" "$TAB_SINK"; then
                info "echo return path already on"
                return
            fi
            # Source from the virtual mic, NOT from bubbles-tts-sink.monitor. The
            # module-remap-source backing the virtual mic consumes that monitor, and other
            # clients reading it get silence. Sourcing the mic is also the truer
            # simulation: it is literally the signal StreamYard receives.
            local id
            id=$(pactl load-module module-loopback \
                source="$VIRTUAL_MIC" \
                sink="$TAB_SINK" \
                latency_msec="$delay" \
                'sink_input_properties=media.name="Bubbles\ echo\ (rehearsal)"')
            remember_module "$id"
            info "echo return path ON: $VIRTUAL_MIC -> $TAB_SINK at ${delay}ms (module $id)"
            echo
            echo "  Bubbles' voice now lands back in the capture mix, ${delay}ms late — the same"
            echo "  loop StreamYard creates. Without the self-suppression gate this will show up"
            echo "  in the transcript. That is the point: it is how you prove the gate works."
            ;;
        off)
            local found=0
            pactl list short modules \
                | grep "module-loopback" \
                | grep "source=$VIRTUAL_MIC" \
                | grep "sink=$TAB_SINK" \
                | awk '{print $1}' \
                | while read -r id; do
                    pactl unload-module "$id" 2>/dev/null && info "unloaded echo module $id" || true
                done
            found=1
            [[ "$found" == "1" ]] && info "echo return path OFF"
            ;;
        *)
            die "usage: $0 echo {on|off}   (delay via ECHO_DELAY_MS, default 300)"
            ;;
    esac
}

case "${1:-}" in
    up)     cmd_up ;;
    down)   cmd_down ;;
    status) cmd_status ;;
    route)  cmd_route ;;
    play)   shift; cmd_play "$@" ;;
    echo)   shift; cmd_echo "$@" ;;
    *)
        echo "Usage: $0 {up|down|status|route|play <file>|echo on|echo off}" >&2
        exit 1
        ;;
esac
