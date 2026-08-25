# Bubbles (Linux / GTK4)

A duplicate of `src/Bubbles` for .NET MAUI's experimental GTK4 backend, so the display can
run on the Linux host instead of a tablet.

It is a **duplicate, not another target framework** on the existing project, because the
GTK backend is a structurally different kind of project: a plain `Microsoft.NET.Sdk`
executable on `net10.0`, with no MAUI single-project support. `src/Bubbles` is untouched
and still builds and deploys to Android exactly as before.

```bash
dotnet run --project src/Bubbles.Gtk
```

## Configuration

| | |
|---|---|
| `BUBBLES_API` env var | API address — takes precedence over everything |
| `~/.config/bubbles/settings.json` | remembered after a successful connection |
| default | `http://localhost:5141` |

Accepts a bare host, `host:port`, or a full URL. A bare host gets **http**, not https —
the mobile app hard-codes https because it talks across the network, but this build
normally runs on the same box as the API.

```bash
BUBBLES_API=http://192.168.1.50:5141 dotnet run --project src/Bubbles.Gtk
```

There is no prompt for the address: the mobile app uses a CommunityToolkit popup, which
has no GTK support. A config file is a better fit here anyway, since it lets the display
start unattended.

## What is different from the tablet app, and why

The GTK backend implements a good set of MAUI handlers — including `GraphicsView`, which is
what makes this viable — but it is experimental and much of Essentials is missing.

**No Lottie, no SkiaSharp.** The tablet app draws its states with `SKLottieView` and its
bubbles with `SKConfettiView`. SkiaSharp registers no handler on GTK, so none of it
renders. The animations in `src/Bubbles/Resources/Raw/*.json` have **no equivalent here** —
the whole visual is redrawn with `Microsoft.Maui.Graphics` in `Drawing/BubblesScene.cs`:
beer gradient, foam head, rising bubbles, and a per-state indicator.

This means the moderator app's custom-animation controls (`SetThinkingStateAnimation`,
`SetSpeakingStateAnimation`, `TestCustomState`) have nothing to switch to. The connection
still receives them; the scene ignores them.

| State | Tablet | GTK |
|---|---|---|
| Idle / Listening | no animation | beer and bubbles only |
| Thinking | `Bubbles.json` | orbiting dots in a breathing ring |
| Speaking | `bizz.json` | animated bar meter |

Bubble density rises with the state, so what Bubbles is doing reads from across a room.

**No XAML.** No MAUI single-project means no XAML compilation, so the UI is C# markup.
`Resources/Styles/*.xaml` do not apply; the two beer colours are duplicated as constants in
`BubblesScene` (`#FFDF20` → `#FFB900`, matching `BeerBackground`).

**No `MainThread`, no `Preferences`.** Both throw
`NotImplementedInReferenceAssemblyException` on GTK. `IDispatcher` is properly wired, so
`Dispatcher.Dispatch` is used to marshal SignalR callbacks; settings go to the config file
above.

**`BubblesConnection` duplicates `UI_Common.ConversationStateService`** rather than
referencing it — that project multi-targets the mobile platforms and depends on both
`Preferences` and the popup. This build is receive-only, so the moderator send methods are
dropped. If the hub message contract changes, both need updating.

## Package version

The sample in `/scratch` pins `Microsoft.Maui.Platforms.Linux.Gtk4` at `0.6.0-*`, which
**does not exist on nuget.org** — only `0.1.0-preview.*` is published. This project pins
`0.1.0-preview.12.26421.1`, the latest available. If you have access to a feed that
publishes 0.6.x, bumping it is the first thing to try when something behaves oddly.

## Possible replacement

[Avalonia.Controls.Maui](https://github.com/AvaloniaUI/Avalonia.Controls.Maui) may make this
whole project unnecessary — the hypothesis (unverified) is that it can be added to the
existing `src/Bubbles` app to render on Linux, rather than needing a separate codebase with a
hand-drawn scene. Worth a spike before investing further here. See `docs/backlog-v3.md`.

## Known gaps

- **Not fullscreen.** MAUI has no cross-platform fullscreen API and the GTK backend exposes
  no window hook for it. Use the window manager (F11 on most desktops) for now.
- **The logo is loaded from disk**, not `MauiImage`, so it is not density-scaled.
- Startup logs two harmless warnings from MAUI itself (a dispatcher provider replacement
  and an alert manager subscription).
