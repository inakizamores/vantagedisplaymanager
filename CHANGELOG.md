# Changelog

All notable changes to Vantage Display Manager are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Reverted
- The accent-palette rework shipped in 0.4.0-beta is backed out. Accent-filled surfaces go
  back to the base accent color with white text, and the HDR badge on profile thumbnails goes
  back to plain white. 0.4.0-beta still contains the change; this reverts it for whatever
  ships next.

## [0.4.0-beta] — 2026-08-04

### Changed
- **New logo and branding.** The mark is now two display panels angled inward as if seen from
  above, with the gap between them as the vantage point — it says what the app does and still
  resolves to a legible V at 16 px, where a tray-first app spends most of its life. Replaces
  the generic gradient tile with a "V" set in Segoe UI. Applies to the app icon, window icon,
  tray icon, installer icon, and the README.
- Brand assets are now generated from one geometry definition by `build/make-branding.ps1`
  (supersedes `build/make-icon.ps1`): the multi-resolution `.ico`, the README lockups for
  GitHub's light and dark themes, a 512 px mark, and a 1280×640 social preview.
  [`docs/assets/vantage-mark.svg`](docs/assets/vantage-mark.svg) is the vector source of truth.
- Icon sizes at or below 24 px render the mark 10% larger, so the V keeps its weight in the
  tray and taskbar.
- The Velopack installer shows a branded splash banner, with the progress bar tinted to the
  accent cyan.
- Release notes now open with a branded banner.
- Screenshots refreshed, and the layout and preset editors documented for the first time.

### Fixed
- **Every icon was shifted one pixel left.** The `.ico` writer declared `biSize = 40` for the
  `BITMAPINFOHEADER` but only wrote 36 bytes, omitting `biClrImportant`. Decoders read the
  promised 40 and swallowed the first four bytes of pixel data — exactly one pixel at 32 bpp —
  shifting every BMP entry (16/20/24/32/48/64) a pixel left and wrapping a column in from the
  next row. This is why small icons looked off-centre and malformed. The bug predates the
  rebrand: it was inherited from `build/make-icon.ps1`, so the old icon was shifted too.
- Icons at or below 32 px are now drawn as a purpose-made variant rather than a shrunk copy of
  the full mark. The cyan panel sits over the bright end of the tile gradient, where it has
  roughly 1.7:1 contrast against about 6:1 for the white panel; once it was two pixels wide it
  dissolved into the tile and the V read as a single bar. Small sizes use two white panels,
  thicker, converged into a solid vertex.
- Icon entries below 128 px are now supersampled 8× and downscaled on premultiplied alpha,
  instead of relying on GDI+ antialiasing of a thin rotated shape at 16 px.
- `vantagectl.exe` had no icon and showed a bare "vantagectl" description; both exes now
  carry the product icon and a real file description ("Vantage Display Manager" /
  "Vantage CLI"), which is also what the SmartScreen prompt reads.
- Assembly metadata was largely unset — `Authors` was the literal string "Vantage" and there
  was no company, copyright, description, or repository URL.

### Fixed
- Accent color now matches native Windows apps exactly. Accent-filled surfaces (buttons,
  toggles) were painted with the base accent color; Windows itself fills them with the
  **Light2** shade in dark mode and **Dark1** in light mode, and inverts the text on top.
  For a purple accent that meant Vantage drew `#A94DC1` where Settings draws `#DB9EE5`.
- Accent buttons ("Save current setup", "Create preset", "Apply arrangement", hotkey "Save")
  no longer hardcode white text, which was unreadable on the lighter dark-mode accent fill.
  They now follow `TextOnAccentFillColorPrimary`, so the label inverts with the theme.
- The "HDR" badge on profile thumbnails picks black or white by the WCAG luminance of the
  panel behind it, instead of always drawing white — it was invisible on the accent-filled
  primary display.

## [0.3.1-beta] — 2026-08-04

### Added
- **Overwrite button on every profile card** — replaces the profile with your current display
  setup (after confirmation), keeping its identity and hotkey.

## [0.3.0-beta] — 2026-08-04

### Added
- **Output color depth (bpc) is now part of profiles** — set through NVIDIA's own API
  (NVAPI `Disp_ColorControl`) and verified by re-query. HDR presets pin **10 bpc**, SDR
  presets pin strictly **8 bpc**, fixing the washed-out colors caused by the driver keeping
  the wrong depth across HDR toggles. Current bpc shows in the app and `vantagectl list`.
  On non-NVIDIA GPUs the feature quietly steps aside (AMD/Intel planned).

## [0.2.1-beta] — 2026-08-04

### Fixed
- Profile thumbnails: the HDR marker is now a crisp "HDR" text badge in the panel's top-left
  corner with even padding, instead of a stretched strip.

## [0.2.0-beta] — 2026-08-04

### Added
- **In-app preset editor** — "New preset…" builds a profile with a different resolution,
  refresh rate, or HDR state per display, straight from dropdowns validated against the
  driver's mode list. No more CLI required for presets.
- **Global hotkeys** — assign a key combination to any profile (keyboard button on each
  profile card). Hotkeys work system-wide, even when Vantage runs tray-only.
- **Visual layout editor** — "Arrange" opens a drag-and-drop editor (Windows Settings style):
  displays snap to each other's edges, and the arrangement is applied through the verified
  engine with automatic rollback.
- **Layout thumbnails** — every profile card now shows a miniature of its monitor
  arrangement (proportional sizes, accent-colored primary, HDR marker), DisplayMagician-style
  but drawn from your live accent color.
- **Test suite** — 14 engine tests running over display-state fixtures recorded from real
  hardware; runs in CI on every push.

### Fixed
- Dialogs now close with Esc.

## [0.1.3-beta] — 2026-08-04

### Changed
- **Profiles and settings now live in `Documents\Vantage Display Manager`** (game-save style):
  they survive uninstall/reinstall, are trivial to back up or copy to a new PC, and sync
  automatically when Documents is OneDrive-redirected. Existing data from the old location
  (`%LOCALAPPDATA%\Vantage`) is migrated automatically on first run.

## [0.1.2-beta] — 2026-08-04

### Fixed
- Taskbar showed a blank icon: the app icon used PNG-compressed frames at all sizes, which
  the Windows shell cannot decode below 256 px. Small frames are now classic BMP entries.

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
