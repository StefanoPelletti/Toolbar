---
name: verify
description: Build, launch, and observe the Toolbar WPF app to verify changes at runtime. Use after modifying Toolbar source to confirm behavior end-to-end.
---

# Verifying Toolbar changes at runtime

## Build & launch

```powershell
dotnet build Toolbar/Toolbar.csproj -c Debug     # exe: Toolbar/bin/Debug/net10.0-windows/Toolbar.exe
```

- **Single-instance mutex**: a running copy (usually the release exe, check
  `Get-Process Toolbar`) blocks new launches. Close it first by posting
  WM_CLOSE (0x0010) to its main window — that path persists config. Never
  `Stop-Process`; it loses the debounced save.
- The user's real instance normally runs from
  `C:\Users\pelle\OneDrive\Desktop\42\Apps\Toolbar.exe`. Relaunch it when done.

## Config harness

Config lives at `%APPDATA%\Toolbar\config.json`. **Back it up before tests and
restore it after** — the app rewrites it on close, so restore must happen after
process exit. Craft minimal test configs to force scenarios (positions are
keyed by display signature; copy the current key from the real config).
Shortcut icons come from `%APPDATA%\Toolbar\iconcache`.

## Observing (no interactive input available)

All observation scripts must run in **Windows PowerShell 5.1** (`powershell.exe`,
C# 5 syntax only in Add-Type — no inline `out var`) and call
`SetProcessDPIAware()` first, or every rect/capture is wrong on this 175%-scaled
machine (physical px = DIP × 1.75).

- **Find window / read position**: EnumWindows filtered by pid, main window has
  title `Toolbar` (SearchWindow has empty title). GetWindowRect gives physical
  px — this is how you verify dock/conceal/restore geometry.
- **Render capture**: PrintWindow with PW_RENDERFULLCONTENT (flag 2) into a
  GDI+ bitmap — captures only the app's pixels, no desktop. See
  scratchpad `capture.ps1` pattern from past sessions.
- **Drive the app**: posting `WM_HOTKEY` (0x0312, wParam 0xB001) to the main
  window triggers the summon/dismiss toggle and opens the search palette —
  this is the one reliable synthetic entry point. Posted WM_LBUTTONDOWN /
  WM_KEYDOWN do **not** work (WPF validates real focus/capture), and real
  SendInput is off-limits (lands in the user's session). Anything requiring a
  genuine click/keypress can't be driven headlessly — verify around it and say
  so.

## What to check per area

- **Geometry (position restore, docking, auto-hide)**: launch with a crafted
  position + `AutoHide` in config, sample GetWindowRect over time. Concealed
  bar leaves a 4-DIP strip on-screen. All config coordinates are DIPs.
- **Rendering**: PrintWindow capture, upscale 3-5x nearest-neighbor to read.
- **Single-instance / update flows**: watch process lifetimes with Get-Process.
