# Changelog

All notable changes to Vantage Display Manager are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [0.1.1-beta] — 2026-08-04

### Added
- **Start with Windows** setting — registers a lightweight sign-in launch that opens straight
  to the system tray (no window). The registered path self-heals if the app moves.
- Settings section in the app.

### Changed
- **Fully automatic failure handling** replaces the "Keep changes?" countdown: every apply is
  verified against Windows; a hard failure (wrong geometry, display lost) restores the
  previous configuration automatically, while soft issues (e.g. HDR didn't verify) keep the
  new configuration and surface a warning. No confirmations, no timers.

## [0.1.0-beta] — 2026-08-04

First public beta. Core engine + native Windows 11 app + CLI.

### Added
- **Display profiles**: capture the complete current setup (layout, resolution, refresh rate,
  rotation, primary display, per-monitor DPI scale, HDR state, SDR white level) and re-apply
  it with one click, one hotkey-ready CLI call, or from the tray.
- **Verified apply pipeline**: validate → apply → wait for Windows to settle → re-capture and
  verify. No blind sleeps, no "trust the API" — if Windows didn't do it, Vantage tells you.
- **15-second auto-revert** after every apply from the app — a bad switch can never strand you
  on a black screen.
- **Profile variants / presets**: derive new profiles (different resolution, refresh, HDR)
  directly from the current setup — no round-trip through Windows Settings. Neighboring
  displays stay glued to the resized display's edge.
- **Per-display HDR toggles** using the Windows 11 24H2 HDR API with automatic fallback to the
  legacy Advanced Color API on older builds.
- **Stable monitor identity**: profiles key on EDID vendor/product/serial — they survive
  reboots, driver updates, port swaps, and adapter LUID churn (hybrid GPU laptops/desktops).
- **Tolerant matching**: profile "active/available" detection compares meaningful fields with
  sensible tolerances (59.94 Hz ≈ 60 Hz), not brittle deep-equality of driver blobs.
- **Native Windows 11 UI**: real OS window frame and caption buttons, Mica backdrop, dark/light
  theme, and accent colors read byte-for-byte from your Windows personalization palette.
- **Tray-first**: closing the window keeps Vantage in the system tray with a profile menu.
- **`vantage` CLI**: `list`, `capture`, `apply`, `active`, `profiles`, `delete`,
  `hdr on|off`, `modes`, `variant` — everything scriptable, JSON output available.
- Versioned, atomic, backed-up JSON profile store (`%LOCALAPPDATA%\Vantage\profiles.json`).

### Known limitations (beta)
- NVIDIA Surround / AMD Eyefinity spanning is not yet supported (planned — see BLUEPRINT M4).
- Per-app automation, hotkeys, and DDC/CI brightness control are planned (M2/M3).
- Windows light/dark theme and accent changes are picked up at launch, not live.
- Unsigned binaries: SmartScreen may warn on first run (More info → Run anyway).
