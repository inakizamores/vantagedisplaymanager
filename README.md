<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/vantage-lockup-dark.png">
  <img src="docs/assets/vantage-lockup.png" width="340" alt="Vantage Display Manager" />
</picture>

### Display profiles for Windows 11 that actually stick.

Save a complete display setup — layout, resolution, refresh rate, HDR, color depth, scaling —
and switch back to it in one click, one hotkey, or one scripted command.
Every switch is verified, and reverts itself if it fails.

<br />

[![CI](https://github.com/inakizamores/vantagedisplaymanager/actions/workflows/ci.yml/badge.svg)](https://github.com/inakizamores/vantagedisplaymanager/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/inakizamores/vantagedisplaymanager?include_prereleases&label=release&color=1E86F0)](https://github.com/inakizamores/vantagedisplaymanager/releases)
[![Downloads](https://img.shields.io/github/downloads/inakizamores/vantagedisplaymanager/total?color=1E86F0)](https://github.com/inakizamores/vantagedisplaymanager/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-3A2FB8.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

<br />

<img src="docs/assets/screenshot-main.png" width="760" alt="The Vantage main window, showing saved display profiles as cards" />

**[⬇ Download](https://github.com/inakizamores/vantagedisplaymanager/releases)** ·
[Quick start](#quick-start) ·
[CLI](#cli) ·
[How it works](#how-it-works) ·
[Roadmap](#roadmap)

</div>

---

## Why Vantage

Tools like HeliosDisplayManagement and DisplayMagician pioneered display profiles on Windows —
and taught everyone the failure modes: profiles that break after every driver update, switches
that hang for 30 seconds, monitors that lose their identity when replugged. Vantage was
designed from a [deep study of those codebases](docs/BLUEPRINT.md) to keep the good ideas and
engineer out the quirks.

The name is the idea: two displays angled toward one seat, and everything about that view saved
as a thing you can return to.

## What it does today

| | |
|---|---|
| 🖥️ **Display profiles** | Capture layout, resolution, refresh rate, rotation, primary display, per-monitor scaling, HDR state, SDR white level, and GPU output color depth — re-apply it all in one verified transition |
| ✅ **Verified switching, automatic recovery** | Every change is validated first, applied, then read back from Windows and confirmed. Hard failures restore your previous setup automatically — no confirmations, no countdowns, no black-screen strandings |
| 🎛️ **Preset editor** | Build "Ultra Wide", "Cinema", "Racing HDR" variants from dropdowns validated against your driver's real mode list — no round-trip through Windows Settings |
| 🧭 **Layout editor** | Drag displays to rearrange them, Windows Settings style, with edge snapping — applied through the verified engine |
| ⌨️ **Global hotkeys** | Assign a key combo to any profile; works system-wide even when Vantage runs tray-only |
| 🎨 **HDR + color depth done right** | Windows 11 24H2 HDR API with legacy fallback, and output bpc pinned per profile via the GPU's own API (10 bpc for HDR, strictly 8 bpc for SDR) — no more washed-out colors from depth stuck between modes |
| 🧬 **Profiles that survive** | Monitors identified by EDID serial — profiles survive reboots, driver updates, port swaps, and hybrid-GPU adapter shuffles |
| 💾 **Reinstall-proof data** | Profiles and settings are plain JSON in `Documents\Vantage Display Manager` — survive uninstalls, copy to a new PC as one folder, ride along with OneDrive |
| 🪟 **Native Windows 11** | Real OS window frame and caption buttons, Mica, dark/light theme, and your exact accent palette from Personalization |
| 🫥 **Tray-first** | Instant start, quiet sign-in launch ("Start with Windows"), profiles one right-click away |
| 🧪 **Tested engine** | Engine test suite runs over display-state fixtures recorded from real hardware, in CI on every push |
| ⌨️ **Fully scriptable** | The `vantagectl` CLI mirrors everything, with JSON output and meaningful exit codes |

## Install

**[⬇ Download the latest release](https://github.com/inakizamores/vantagedisplaymanager/releases)**

| File | For |
|---|---|
| `Vantage-Setup.exe` | **Recommended.** Installs per-user (no admin), Start Menu shortcut, updates cleanly |
| `Vantage-<version>-win-x64-portable.zip` | No install: unzip anywhere and run `Vantage.exe`. Fully self-contained — no .NET required |
| `Vantage-<version>-win-x64-lite.zip` | Small download if you already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

> **SmartScreen note (beta):** binaries are not yet code-signed, so Windows may show
> "Windows protected your PC" on first run. Click **More info → Run anyway**, and verify the
> SHA-256 against `checksums.txt` if in doubt. Code signing is planned via SignPath.

**Requirements:** Windows 10 20H2+ (Windows 11 recommended — HDR features use the 24H2 APIs
when available). x64. No administrator rights needed, ever. Color-depth control currently
requires an NVIDIA GPU (AMD/Intel planned); everything else works on any GPU.

## Quick start

1. Arrange your displays the way you like (the **Arrange** editor or Windows Settings — either works).
2. Type a name → **Save current setup**.
3. Want variants (different resolution, refresh, or HDR)? **New preset…** builds them from
   dropdowns — HDR presets automatically pin 10 bpc output, SDR presets pin 8 bpc.
4. Switch from the app, the tray menu, a **hotkey** (keyboard button on each profile card), or
   a script. Every apply is verified; failures revert automatically.
5. Setup drifted? The **overwrite** button on any profile card re-syncs it to your current
   setup in one click.

<div align="center">
<table>
<tr>
<td align="center" width="50%">
<img src="docs/assets/screenshot-layout.png" width="430" alt="The Arrange displays editor, with two displays being dragged into position" />
<br /><sub><b>Arrange</b> — drag displays, snap to edges, applied through the verified engine</sub>
</td>
<td align="center" width="50%">
<img src="docs/assets/screenshot-preset.png" width="350" alt="The New preset dialog, with resolution and refresh rate dropdowns per display" />
<br /><sub><b>New preset</b> — modes validated against your driver's real mode list</sub>
</td>
</tr>
</table>
</div>

### CLI

```text
vantagectl list                  Show connected displays and their full state
vantagectl capture <name>        Save the current configuration as a profile
vantagectl apply <name>          Apply a profile (validated + verified + auto-revert)
vantagectl active                Which profile matches right now?
vantagectl profiles              List profiles with active/available status
vantagectl hdr on|off [n]        Toggle HDR on all capable displays, or one
vantagectl modes [n]             Supported resolutions/refresh rates per display
vantagectl variant …             Create a preset variant of the current setup
vantagectl snapshot              Full state dump (diagnostics / test fixtures)
```

Add `--json` to `list`, `profiles`, or `active` for machine-readable output.

## How it works

The engine is built on the Windows CCD API (`QueryDisplayConfig` / `SetDisplayConfig`) with a
normalized, versioned profile schema on top:

- **Identity** — monitors are keyed by EDID vendor + product + serial read from the PnP
  registry, with instance-ID fallback. Adapter LUIDs (which change every boot) are re-mapped
  by adapter device path at apply time.
- **Matching** — "is this profile active?" is a per-field semantic comparison with explicit
  tolerances (59.94 Hz ≈ 60 Hz), never a raw struct comparison. A mismatch tells you *what*
  differs.
- **Applying** — validate (`SDC_VALIDATE`) → apply → settle with deadline → per-display
  DPI/HDR/color-depth/SDR-white with verify-by-re-query → final re-capture and match →
  automatic rollback on hard failure. When the same displays stay active, the topology replay
  is skipped and modes are reconciled in a single staged desktop transition.
- **Vendor APIs** — a minimal source-only NVAPI binding covers what Windows can't (output
  color depth), loaded only when the NVIDIA driver is present.
- **Your data** — plain JSON in `Documents\Vantage Display Manager`; survives reinstalls,
  moves to a new PC by copying one folder.

The full design — including the research on DisplayMagician, Helios, Monitorian, twinkle-tray,
HDRTray, and friends that informed it — lives in [docs/BLUEPRINT.md](docs/BLUEPRINT.md) and
[docs/research/](docs/research/).

## Building from source

```bash
git clone https://github.com/inakizamores/vantagedisplaymanager.git
cd vantagedisplaymanager
dotnet build Vantage.sln
dotnet test
```

Requires the .NET 8 SDK. `src/Vantage.App` is the WPF app, `src/Vantage.Cli` the CLI,
`src/Vantage.Core` the engine, `src/Vantage.Interop` the hand-written Win32/CCD/NVAPI layer,
`tests/` the engine test suite. Releases ship automatically when a `v*` tag is pushed
(see [CONTRIBUTING.md](CONTRIBUTING.md)).

## Brand

<img src="docs/assets/vantage-mark-512.png" width="72" align="left" hspace="4" vspace="6" alt="" />

The mark is two display panels angled inward as if seen from above. The gap between them is
where you sit — the vantage point. It resolves to a legible V at 16 px, which is where a
tray-first app spends most of its life.

<br clear="left" />

| | |
|---|---|
| Tile gradient | `#3A2FB8` → `#1E86F0` |
| Active panel | `#5CE1FF` |
| Idle panel | `#FFFFFF` |

[`docs/assets/vantage-mark.svg`](docs/assets/vantage-mark.svg) is the vector source of truth.
Every raster asset — the multi-resolution `.ico`, the README lockups, the release banner, the
social card, and the 493×58 installer splash — is generated from one geometry definition.
Icons at or below 32 px use a purpose-drawn variant of the mark rather than a shrunk copy of
it, because the cyan panel loses its contrast against the tile once it is a couple of pixels
wide:

```powershell
pwsh build/make-branding.ps1
```

## Roadmap

**Next up**
- 🔆 **Brightness & monitor controls (DDC/CI)** — per-monitor brightness from the app and tray
  (SDR-white-level slider under HDR), monitor input switching (DP/HDMI), with the
  crash-sentinel hardening from the research
- 🔄 **In-app auto-update** — the Velopack update feed already ships with every release;
  wiring the app to check GitHub and update itself is the remaining step
- 🎯 **Per-app automation** — "when this game launches: 240 Hz + HDR on; revert when it
  exits" via process events (no launcher catalogs, no polling)

**Then**
- ⏰ Time & event triggers — sunrise/sunset, dock/undock, resume from sleep
- 🔊 Audio device switching per profile
- 🖇️ Desktop shortcuts per profile (with the layout-thumbnail icons)
- 📦 winget package (`winget install vantage`) and code signing (kills the SmartScreen warning)

**Later**
- 🕹️ NVIDIA Surround / AMD Eyefinity spanning — deliberately last: it's the #1 crash source
  in every incumbent, so it only ships with the full validate/verify/rollback treatment
- 🎨 AMD (ADLX) and Intel (IGCL) color-depth backends
- 🪟 Window-layout capture/restore, per-profile wallpaper
- 💻 ARM64 builds

**Shipped so far** — see the [changelog](CHANGELOG.md): verified profile engine with automatic
rollback (0.1.x), preset editor + hotkeys + layout editor + layout thumbnails + engine tests
(0.2.x), per-profile GPU color depth + profile overwrite (0.3.x).

## Credits

Vantage stands on the shoulders of open-source pioneers:
[HeliosDisplayManagement](https://github.com/falahati/HeliosDisplayManagement) ·
[DisplayMagician](https://github.com/terrymacdonald/DisplayMagician) ·
[Monitorian](https://github.com/emoacht/Monitorian) ·
[twinkle-tray](https://github.com/xanderfrangos/twinkle-tray) ·
[HDRTray](https://github.com/res2k/HDRTray) ·
[AutoActions](https://github.com/Codectory/AutoActions) ·
[LittleBigMouse](https://github.com/mgth/LittleBigMouse) ·
[SetDPI](https://github.com/imniko/SetDPI) ·
[WPF-UI](https://github.com/lepoco/wpfui) ·
[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)

## License

[MIT](LICENSE) © 2026 Iñaki Zamores
