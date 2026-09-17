# Changelog

All notable changes to Vantage Display Manager are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [1.0.3] — 2026-09-17

### Changed
- **Updates are now delta downloads.** Updating from the previous version transfers only what
  actually changed — about 2 MB instead of the full ~75 MB package (1.0.2 → 1.0.3 touched 17
  of 477 files). The full package is still
  published, so a client that can't apply a delta (or is coming from an older version) simply
  falls back to it. Delta generation was never switched off; the release pipeline just never
  gave the packer a previous release to compare against, so every release so far shipped
  full-only.
- **UI framework and dependencies updated** — WPF-UI 3.0.5 → 4.3.0, CommunityToolkit.Mvvm
  8.3.2 → 8.4.2, H.NotifyIcon.Wpf 2.1.3 → 2.3.2, and the test stack to xunit 2.9.3 with
  Microsoft.NET.Test.Sdk 18.10.1.

### Fixed
- **A release can no longer publish itself half-finished.** GitHub's asset upload endpoint
  intermittently answers HTTP 500 on the larger files, and on 1.0.2 that left a release
  carrying two of its six files — with an update feed pointing at packages that weren't
  there. The pipeline now builds each release as a draft, uploads one file at a time with
  retries, and refuses to make the release visible until it has verified every file is
  present.

## [1.0.2] — 2026-09-17

### Fixed
- **Identical monitors no longer break saving, applying or refreshing.** A monitor's identity
  was its EDID vendor + product + serial, but plenty of panels ship with the same serial on
  every unit of a model — three ASUS ROG Strix XG438Q all report `AUS43E1_125727`. That made
  the identity a *model* id, so the display list was keyed on duplicate keys and any refresh
  or save failed with "An item with the same key has already been added. Key: …"
  ([#9](https://github.com/inakizamores/vantagedisplaymanager/issues/9)). Identity now falls
  back to the GPU connector each panel is plugged into when, and only when, a serial is
  shared — so ids are unchanged on every machine that never had a collision, and the panels
  that did have one keep distinct, reboot-stable identities instead of being numbered in
  whatever order Windows happened to enumerate them.
- **Existing presets are repaired automatically.** A preset saved with the old colliding ids
  is re-keyed on load from the connector path it already recorded, so it keeps matching the
  machine it was captured on with nothing to redo. Matching also gained a last-resort pass
  that pairs same-model panels by position, so a preset still applies after its cables are
  swapped between ports.
- **Identical monitors are now distinguishable in the app.** Displays sharing a model name
  are labelled with their Windows display number ("ASUS XG438 (Display 2)") instead of
  appearing as several identical rows.

### Changed
- Indexing displays by identity can no longer throw anywhere in the codebase: a duplicate is
  logged and the first entry kept, so an unforeseen collision costs one display's worth of
  precision rather than the whole feature.
- Test suite grew from 37 to 60, over a fixture recorded from the reporting machine's three
  identical panels.

## [1.0.1] — 2026-08-09

### Fixed
- **Preset shortcuts now survive version updates.** Installing a new version by running its
  Setup.exe over an existing copy uninstalls the old version first — whose uninstall hook
  deleted every preset shortcut, leaving Start pins dead until each one was recreated by
  hand. The install and update hooks now restore every recorded shortcut instead: a missing
  `.lnk` is recreated at the exact path it lived at (which revives any Start pin made
  against it), a surviving one is re-pointed in place. Repairs now also fix a shortcut
  whose icon file went missing, not just its target.

### Added
- **Settings → Repair preset shortcuts.** The same restore, on a button — for shortcuts a
  past update already broke, or any other time a `.lnk` stops working.

## [1.0.0] — 2026-08-09

The first stable release. Everything the betas built — the verified apply engine with
automatic rollback, presets, hotkeys, the layout editor, Start menu shortcuts, per-profile
GPU color depth, in-app updates — plus the hardening pass that makes it trustworthy enough
to drop the `-beta`: nothing fails silently anymore, nothing crashes without a trace, and a
damaged profile file can no longer take the app down with it.

### Added
- **A diagnostic log.** `vantage.log` in `Documents\Vantage Display Manager` (rolled at
  512 KB, bounded at ~1 MB), fed by unhandled-exception handlers on every path — the WPF
  dispatcher, background threads, headless launches. A failed apply writes the engine's full
  step log there; "Apply failed — see log" now points at a log that exists.
- **Failures reach you in the tray.** The app's error surface used to be an InfoBar inside a
  window that is hidden for most of this app's life. Warnings and errors now fall back to a
  tray notification when the window isn't visible — a failed hotkey apply, tray-menu apply,
  or out-of-date shortcut no longer vanishes without a word.
- **First-run guidance.** A brand-new install shows what to do (arrange → name → save)
  instead of an unexplained gap between the save row and Settings.
- **Real settings.** Close-to-tray vs. exit is now a choice; the startup update check has an
  off switch; and an "Open folder" button jumps to your data. Stored in `settings.json`
  beside the profiles, atomically, versioned.
- **Live theme and accent tracking.** Switching Windows between light and dark, or changing
  the accent color, restyles Vantage on the spot — window, title bar, and the profile
  thumbnails that paint the primary display in your accent. Previously picked up only at
  launch, which a tray-resident app might not see again for weeks.
- **Hotkey conflicts are caught when you set them.** Assigning a combo another profile
  already owns, or one another app holds system-wide, is refused on the spot with the actual
  holder named. Conflicts found later name the profile, not just the gesture, and warn once
  instead of after every refresh.
- `Vantage.exe --help` (also `-h`, `/?`) prints the launch switches instead of silently
  opening the GUI.
- `vantagectl` usage now lists `snapshot`, and every hint it prints uses its real name —
  it used to suggest `vantage …` commands that don't exist.

### Fixed
- **A damaged `profiles.json` no longer prevents Vantage from starting.** It is quarantined
  under a timestamped name, the automatic `.bak` is restored if readable, and the app tells
  you what happened. Previously: an unhandled parse error before the window existed.
- **Concurrent profile writes can no longer lose a profile.** The store file is guarded by a
  cross-process lock — the window, the background shortcut reconciler, and headless
  `--apply` processes all serialize their read-modify-writes.
- The update dialog survives a mid-download failure without risking the process, cancels the
  download when closed from the title bar, and shows failures in the system critical color
  instead of subtitle grey.
- Portable and unmanaged copies get a working "Releases page" button instead of a check
  button that can only report why it can't work.
- The IPC listener thread can no longer take the whole app (tray icon, hotkeys and all) down
  with it on an unexpected fault.
- Deleting a profile now says its Start menu and desktop shortcuts go with it.
- Main window title now reads "Vantage Display Manager", matching everything else.

### Changed
- **Installed 1.0 copies follow the stable channel.** Beta prereleases are no longer offered
  to a stable install; the 0.x betas still see stable releases and update onto 1.0.0.
- Release engineering hardened for 1.0: the release workflow now runs the test suite before
  packing, refuses a tag whose version disagrees with the repo or whose changelog section is
  missing, pins the Velopack packer to the version the shipped updater understands, and
  stamps the tag's version into the binaries' file metadata (previously frozen at the last
  props value — Explorer would have called 1.0.0 "0.6.3").
- Test suite grew from 20 to 37: profile-store corruption recovery, concurrent-writer
  safety, legacy data migration, and the launch-switch grammar every shortcut depends on.

## [0.6.3-beta] — 2026-08-09

### Fixed
- The version card in Settings sat empty under its title until you pressed Check for updates —
  the line below the version was bound to a status that is deliberately blank when the silent
  startup check finds nothing. It now carries a description of what the app does on its own,
  the same way the Start with Windows card above it explains itself.

## [0.6.2-beta] — 2026-08-09

### Fixed
- `--update` reported an installed copy as "not installed by the setup program" and refused to
  update it. `VelopackApp.Run()` is what tells Velopack where the app lives, and the new switch
  returned before reaching it — so the locator found nothing and `IsInstalled` came back false.
  The hooks now run on that path too. The in-app Update button was never affected; it goes
  through the normal startup path, which always ran them.

### Added
- `Vantage.exe`'s own switches (`--apply`, `--tray`, `--update`) are documented in the README
  alongside the `vantagectl` commands, with their exit codes.

## [0.6.1-beta] — 2026-08-09

### Added
- **`Vantage.exe --update`** — the whole update flow without the window: checks GitHub, reports
  the version and release-note size it found, downloads with progress, installs and relaunches.
  Scriptable, and the reason the update path can be verified end to end rather than clicked
  through by hand. Exit codes: 0 updated, 1 already current, 2 not an installed copy, 3 failed.
  It runs before the single-instance check, since updating is Velopack's business whether or not
  a copy is already running.

## [0.6.0-beta] — 2026-08-09

### Added
- **In-app updates.** Vantage now checks its own GitHub releases, shows what changed, and
  installs the new version itself. The check runs quietly in the background at launch — nothing
  pops up; the Settings card simply starts offering an Update button instead of a Check one.
  Choosing it opens the release notes for that version, then downloads with a progress bar and
  restarts. Profiles, settings and shortcut icons live in `Documents\Vantage Display Manager`,
  outside the install folder, so an update never touches them, and preset shortcuts are
  re-pointed by the after-update hook below. An update is refused outright while a display
  change is in flight rather than swapping the app out mid-switch.
- Release notes are embedded in the update package itself by the release workflow, extracted
  from this changelog, so the app can show what a version contains before installing it without
  a round-trip to the GitHub API — and the release page and the app can never disagree.
- **Presets as Start menu shortcuts.** The pin button on any profile card turns that preset into
  a Windows shortcut, in the Start menu, on the desktop, or both. Type the preset's name into
  Start, press Enter, displays switch — no window opens. Shortcuts land in the All apps root
  alongside your other apps; grouping them under a "Vantage Presets" folder is a checkbox, off
  by default, because a folder is a collapsed heading you have to expand every single time.
  Everything is per-user, so none of it needs administrator rights.
- **Per-preset icons.** Each shortcut gets its own multi-resolution `.ico` rendered from the
  preset's monitor-layout thumbnail — the same picture the profile card shows — written at
  16/20/24/32/48/64/128/256 px so the Start menu, taskbar and Explorer each get a crisp size.
  The HDR badge is dropped below 128 px, where it would only ever be a smudge. Any image,
  `.ico` or program can be used instead. Icons live in
  `Documents\Vantage Display Manager\Shortcut Icons` so they survive updates and reinstalls,
  and are written under a fresh name each time because Explorer caches icons by path.
- **`--apply <profile id or name>`** on `Vantage.exe`, the switch shortcuts carry. If Vantage is
  already in the tray the launched process forwards the request over a named pipe and exits,
  and the warm instance — which already has the display service and profile store loaded — does
  the switch. If nothing is running, the profile is applied with no theme, no tray icon, no
  view model and no window built at all. Failures on the cold path surface as a message box,
  since there is no window to put an info bar in.
- **The app now owns its entry point** (`Program.Main`, replacing the one WPF generates) so both
  of those paths are decided *before any WPF type is loaded*. Constructing the WPF `Application`
  and merging App.xaml's theme dictionaries measures ~90 ms, which a process that will never
  draw anything should not pay. A shortcut handover is ~36 ms of total process lifetime (the
  pipe round trip inside that is ~3 ms), and a cold start reaches profile resolution in ~166 ms.
  `VelopackApp.Run()` moved to the top of the real startup path, which is where Velopack has
  been asking for it.
- **Preset shortcuts survive updates, moves and reinstalls.** A `.lnk` stores an absolute
  path, so anything that relocates the executable would leave every preset shortcut pointing at
  a file that no longer exists. Velopack solves this for its own Start menu entry by rewriting
  it on each update; preset shortcuts now get the same treatment, from three places:
  Velopack's after-install and after-update hooks (fixed at the moment the thing that would
  break them happens), and every normal launch as a safety net for moves Velopack knows nothing
  about — a portable copy unzipped somewhere new, for instance. The launch check is off the
  startup path and costs one string compare per shortcut when nothing has moved.
- **Uninstalling removes preset shortcuts** via Velopack's before-uninstall hook, instead of
  leaving dead entries behind in the Start menu.
- Deleting a profile removes its shortcuts and generated icon too.
- Tests for the engine's wait loop: returns as soon as the hardware agrees, honours the full
  budget when it doesn't, survives a transient `CcdException` while an output re-trains, and
  cancels cleanly. `Vantage.Core` now exposes internals to the test assembly.

### Changed
- **The apply engine no longer sleeps out its worst case.** Three steps ended in a fixed
  `Task.Delay` before checking whether the change had landed — DPI 150 ms, HDR 300 ms, colour
  depth 600 ms per attempt — and the settle wait polled only every 250 ms. So an HDR preset that
  also pins colour depth paid ~900 ms of unconditional waiting plus up to 250 ms of settle
  overshoot, no matter how fast the hardware actually was. All four now poll every 40 ms against
  the same budgets, and the DPI step (whose setter is synchronous) checks before waiting at all.
  Slow hardware still gets every millisecond it had; fast hardware stops paying for it. This was
  the last of the "sleep engineering" the research faulted the incumbents for (BLUEPRINT P2/P7).
- Measured for context: `DisplayService.Capture()` is 2.7 ms median on a two-display setup, and
  an apply calls it four times — so the engine's own bookkeeping is ~18 ms. Loading the profile
  store is 0.2 ms and matching is under 0.1 ms. Essentially all remaining latency is the display
  pipeline itself.
- `vantagectl apply` now stamps every step with elapsed milliseconds and reports a total, so
  where the time goes is visible rather than inferred.

### Fixed
- Editing a shortcut deleted and recreated the `.lnk` even when the path was unchanged, which
  drops any Start pin made against it. Shortcuts whose path survives an edit are now overwritten
  in place; only genuinely stale paths (renamed, or moved between the Start menu and the
  desktop) are deleted. Repairs preserve the icon and arguments too.
- A shortcut deleted from Explorer is now forgotten rather than kept in the profile record — and
  is never silently recreated, since deleting it was a deliberate act.
- A second launch of Vantage tried to release a mutex it never owned, throwing on exit. Never
  noticeable before because second launches did nothing; now every shortcut click is one.
  Replaces the `TODO(M1)` placeholder with the real named-pipe channel: a plain second launch
  brings the running window to the front instead of vanishing silently.

### Notes
- Windows does not let an application pin itself to Start. The "Pin to Start" verb is listed on
  a shortcut but invoking it programmatically is a no-op — verified against the pinned-items
  database, which does not change. Pinning a preset stays a one-time right-click.

## [0.4.5-beta] — 2026-08-04

### Fixed
- The profile name box and the "Save current setup" / "New preset…" buttons beside it were
  different heights, so the row looked ragged. All three now stretch to the row height and
  line up top and bottom.
- Same on every profile card: the buttons carrying text (hotkey, "Apply") rendered 2 px taller
  than the icon-only ones (overwrite, delete) and sat a pixel higher. All four now stretch to
  the row height — measured identical at 31 px.

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
