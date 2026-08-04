# Vantage: Display Manager — Master Blueprint

**Status:** Research phase complete (2026-08-03). This document synthesizes the four research
reports in [`docs/research/`](research/) into the design for Vantage.

Vantage is a next-generation display manager for Windows 11+: save and switch complete display
setups (layout, resolution, refresh rate, rotation, HDR, DPI scaling, primary monitor), control
brightness and monitor inputs, and automate all of it per-app, per-time, or per-hotkey — behind
a native Windows 11 UI that starts instantly from the tray.

It is the spiritual successor to HeliosDisplayManagement → DisplayMagician, built on their
hard-won domain knowledge but engineered specifically against their failure modes.

---

## 1. The market gap (why Vantage should exist)

From [`research/ecosystem-and-platform.md`](research/ecosystem-and-platform.md) §2: no tool
combines all of the following, and every incumbent that does one has a dated UI or fragile
monitor matching:

| Capability | Incumbent that does it | Their weakness |
|---|---|---|
| Topology/display profiles | DisplayMagician, DisplayFusion, Monitor Profile Switcher | Brittle equality, lost profiles on ID shifts, unmaintained |
| HDR / SDR white level / refresh / DPI in profiles | Nobody (HDRTray does HDR only) | — |
| DDC/CI brightness + input switching | Monitorian, Twinkle Tray, vendor tools | Brightness-only, Electron, or single-brand |
| Per-app / time automation | AutoActions, Twinkle Tray | Polling-based, weak monitor identity |
| Native Win11 UI, tray-first, instant start | Nobody in this category | — |

Windows 11 itself (even 24H2/25H2) still has **no display profiles at all**.

## 2. Non-negotiable design principles

Each principle is traced to an observed failure in the incumbents (evidence in the linked
reports).

### P1. Semantic profiles with tolerant matching — never deep struct equality
DisplayMagician's #1 community complaint ("profile not valid / can't be used" after any driver
update) is caused by defining profile identity as deep equality over raw dumps of four vendor
APIs, with fields commented out one by one as they were discovered to churn
(`displaymagician-analysis.md` §2.4, §8.3).

**Vantage:** a profile is a **normalized, versioned schema** — per display: stable monitor
identity, position, resolution, refresh (millihertz), rotation, scaling mode, DPI scale, HDR
state, SDR white level, bit depth, primary flag — plus an *opaque* raw CCD replay payload used
only for applying, never for identity. "Is this profile active/possible?" is a **field-by-field
semantic comparison with explicit tolerance rules** (e.g. 59.94 Hz ≈ 60 Hz handling, ignore
vendor-private churn) that returns a scored diff, not a boolean from `Equals`.

### P2. Event-driven, verified, async apply — never Thread.Sleep orchestration
Helios sleeps 2 s + 18 s + 10 s per switch; DisplayMagician sleeps between every step, retries
`SetDisplayConfig` three times on a timer, and blocks the UI thread for the whole apply
(`helios-and-falahati-libs.md` §1.2, `displaymagician-analysis.md` §8.2, §8.9).

**Vantage:** the apply pipeline is an async state machine: validate (`SDC_VALIDATE`) → apply →
**wait on display-change events** (`WM_DISPLAYCHANGE` + re-query) with timeout → **verify by
re-capturing** and semantically comparing → next stage. Every setter is verified by re-query
(HDRTray/Monitorian both prove setters lie — `auxiliary-projects.md` §3, §1). Progress is
reported via `IProgress<ApplyStep>`, cancellable, never on the UI thread. A timed auto-revert
("Keep these settings?" 15 s countdown, like Windows itself) guards against black-screen
outcomes.

### P3. One monitor-identity service, built on stable IDs
Adapter LUIDs change every reboot; Windows display numbers and CCD target IDs are
session-scoped; docks/USB-C shift device paths — the root cause of lost profiles in
DisplayFusion, Monitor Profile Switcher, and AutoActions.

**Vantage:** a single identity service (modeled on Monitorian's four-source join,
`auxiliary-projects.md` §1) keyed on **device instance ID**, enriched with **parsed EDID
vendor + product + serial** (registry `SetupDiOpenDevRegKey` path per LittleBigMouse, plus
`WmiMonitorID` and WinRT `DisplayMonitor`), with `NOEDID_*` synthetic fallback. Session-scoped
handles (`DisplayIdSet` = adapter LUID + source id + target id) are resolved fresh each session;
saved profiles store only stable identity. LUID re-mapping by adapter device path at load time
(DisplayMagician's `PatchWindowsDisplayConfig` technique) patches the raw replay payload.

### P4. One owned CCD interop core with OS-version gating
All of it — topology, target names, HDR (both API generations), SDR white level, DPI scaling —
flows through `QueryDisplayConfig` / `SetDisplayConfig` / `DisplayConfigGet/SetDeviceInfo`.

**Vantage:** a single source-generated interop layer (CsWin32 / `[LibraryImport]`) covering:
- Documented: paths/modes capture-replay (`QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE`;
  apply with `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES |
  SDC_SAVE_TO_DATABASE`, topology-only fallback), `GET_TARGET_NAME`, `GET_ADVANCED_COLOR_INFO`,
  `SET_ADVANCED_COLOR_STATE`, `GET_SDR_WHITE_LEVEL`.
- Win11 24H2+ (runtime-gated, HDRTray pattern): `SET_HDR_STATE`, `GET_ADVANCED_COLOR_INFO_2`
  (never interpret `advancedColorEnabled` as "HDR on" where ACM exists).
- Undocumented (feature-flagged, struct-size tripwires, fail-soft): DPI scale get/set (types
  −3/−4, relative-step model, snap to the 100–500% table, applied to the **source**), SDR white
  level set (`0xFFFFFFEE`, nits×1000/80), `InternalRefreshCalibration` after HDR toggles.
- Clone topology handling: virtual-target patching at capture, rebuild-from-current-targets at
  apply, `SDC_TOPOLOGY_CLONE` database fallback (DisplayMagician's proven recipe).

No admin rights are required for any of this — Vantage runs `asInvoker`.

### P5. Vendor SDKs are optional, isolated, and deferred
NVIDIA Surround is the single largest source of incumbent bugs (crashes, black screens
requiring reboot — ecosystem report §1.2). Both falahati wrappers are dead; DisplayMagician
vendors closed-source wrapper DLLs (supply-chain smell).

**Vantage:** an `IGpuVendorService` abstraction, P/Invoked from source (never checked-in binary
wrappers), loaded only when the vendor driver DLL is present. NVAPI binding reuses the
QueryInterface + versioned-struct pattern (documented in `helios-and-falahati-libs.md` §3.2)
with modern source-generated marshaling. **v1.0 ships without Surround/Eyefinity**; the profile
schema reserves a per-display vendor-extras bag (Helios's `SurroundTopology`-as-attachment
shape) so v1.x can add Mosaic/Eyefinity/IGCL without a schema break. Vendor color/bit-depth
overrides (10-bit, dithering) arrive with the same layer.

### P6. Tray-first, instant start
DisplayMagician scans five game libraries before its tray icon appears. **Vantage:** tray icon
first, everything else lazy. No game-library scanning at startup (or at all in v1 — see P9).
Cold start target: < 500 ms to functional tray menu.

### P7. Treat hardware and OS state as hostile and eventually consistent
Independent evidence across three codebases (`auxiliary-projects.md` cross-project section):
- Re-query after every set; the OS lies briefly after `WM_DISPLAYCHANGE` → re-check on decaying
  timers (Monitorian: 5/5/10/10/30 s after resume; HDRTray: 10×500 ms).
- DDC/CI: capability strings lie, monitors hang or crash the caller mid-probe, VCP codes are
  nonstandard, ranges aren't 0–100. Adopt twinkle-tray's **crash-sentinel + per-monitor
  auto-degradation**, per-monitor probe timeouts, parallel probing, single-retry on transient
  errors (Monitorian's status taxonomy), cached capability snapshots, and a shippable
  **monitor quirk database** (`monitor-rules.json` equivalent) updatable out-of-band, plus
  user escape hatches (preclude/preclear).
- Brightness under HDR controls **SDR white level**, not VCP 0x10 — slider semantics switch
  per display.

### P8. Versioned storage with real migrations, safe serialization
DisplayMagician: UTF-16 Newtonsoft with `TypeNameHandling.Auto` (gadget attack surface),
base64 bitmaps inside profile JSON, migration function commented out, format breaks at v2.4 and
Win10→11 that forced users to recreate everything.

**Vantage:** System.Text.Json source-generated, UTF-8, no polymorphic type handling, explicit
`schemaVersion` field, forward-only migration pipeline executed on load, icons/bitmaps cached
as separate files, atomic writes (temp + rename), automatic backup of the profile store before
migration and before every schema-version upgrade.

### P9. Scope v1 to the display domain; automation over launcher catalogs
DisplayMagician's launcher scrapers (regex-parsed VDFs, Origin manifests, steamdb scraping in
Helios with TLS validation disabled) are its highest-maintenance area.

**Vantage v1:** no per-launcher catalogs. Instead a general **automation engine**:
- Triggers: process started/exited/focused (WMI `Win32_ProcessStartTrace`/ETW, not 1 Hz
  polling; `GetForegroundWindow` polling only for focus), display topology changed (dock
  events), time of day / sunrise-sunset, resume from sleep, hotkey, idle.
- Actions: apply profile, HDR on/off, refresh rate, brightness/SDR white level, default audio
  playback/capture device (+ volumes), run/close program, revert-on-exit (the "temporary
  permanence" concept from DisplayMagician, kept — separately for display and audio).
This covers "HDR + 4K120 + headset when the game launches, revert after" without knowing what
Steam is. Launcher-aware shortcuts can come later as sugar on top.

### P10. Testable core, CLI twin, single instance
- Core is DI-composed, capture functions are pure (`ICcdApi` → immutable snapshot models), so
  the matcher/migrations/pipeline are unit-testable against **recorded fixtures** from real
  machines (zero tests is how DisplayMagician ended up where it is).
- `vantage.exe` GUI + `vantagectl` (or `vantage --cli`) headless twin: `list`, `capture`,
  `apply <profile>`, `active`, `hdr on|off`, `brightness`, parseable output + meaningful exit
  codes (HDRCmd model; DisplayMagicianConsole precedent).
- Single instance via mutex + named pipe with command forwarding (DisplayMagician's works);
  validate pipe-client session like LittleBigMouse.

## 3. Technology stack (decided)

| Concern | Choice | Rationale (details: ecosystem report §4) |
|---|---|---|
| Runtime | **.NET 10 LTS**, x64 (+ ARM64 later) | Current LTS; DisplayMagician already proves modern .NET works here |
| UI | **WPF + iNKORE.UI.WPF.Modern** (fallback candidate: WPF-UI) with MVVM (CommunityToolkit.Mvvm) | Native Win11 Fluent 2 look incl. Mica today; WPF = best P/Invoke + tray + fast cold start; WinUI 3 still has no tray story and temp-extract single-exe; .NET's built-in Fluent theme still buggy |
| Tray | **H.NotifyIcon.Wpf** | Maintained, native light/dark menus, efficiency mode |
| Interop | **CsWin32** for Win32/CCD/Dxva2; owned `[LibraryImport]` NVAPI binding; ADLX official C# (SWIG) bindings; IGCL P/Invoke (MIT) | Source, no vendored binary DLLs — reproducible builds |
| Serialization | System.Text.Json (source-generated) | P8 |
| Hotkeys | `RegisterHotKey` + Raw Input; optional gamepad listener later | Not a DirectInput polling thread |
| Audio | CoreAudio via maintained interop (NAudio.CoreAudioApi or direct `IPolicyConfig`) | AudioSwitcher binaries are abandoned |
| Logging | `Microsoft.Extensions.Logging` + NLog/Serilog sink, level-guarded, never inside comparisons | |
| Installer/update | **Velopack** (stable exe path preserves tray pinning; delta updates) + **winget** manifest + portable zip | |
| Signing | **SignPath Foundation** (OSS) — Azure Trusted Signing individual onboarding is paused | |
| Versioning | Nerdbank.GitVersioning | Proven in DisplayMagician |
| Tests | xUnit + fixture recordings of CCD snapshots | P10 |

**Solution layout:**

```
Vantage.sln
├─ src/Vantage.Interop        # CsWin32 + CCD/Dxva2/undocumented packets, NVAPI/ADLX/IGCL bindings
├─ src/Vantage.Core           # UI-agnostic: identity service, capture, matcher, apply pipeline,
│                             #   profile store + migrations, automation engine, brightness, audio
├─ src/Vantage.App            # WPF shell: tray, main window, layout editor, settings, toasts
├─ src/Vantage.Cli            # headless twin (shares Core)
└─ tests/Vantage.Core.Tests   # matcher/migration/pipeline tests over recorded fixtures
```

## 4. Profile schema (v1 sketch)

```jsonc
{
  "schemaVersion": 1,
  "id": "b0c8…",                      // GUID
  "name": "Desk – 4K144 + vertical",
  "hotkey": "Ctrl+Alt+1",
  "displays": [
    {
      "identity": {                    // stable — P3
        "deviceInstanceId": "DISPLAY\\DEL41D9\\5&2f…",
        "edid": { "vendor": "DEL", "product": "41D9", "serial": "ABC123" },
        "friendlyName": "DELL U2723QE"
      },
      "enabled": true, "primary": true,
      "position": { "x": 0, "y": 0 },
      "mode": { "width": 3840, "height": 2160, "refreshMillihertz": 143856 },
      "rotation": "landscape", "scalingMode": "preserveAspect",
      "dpiScalePercent": 150,
      "hdr": { "enabled": true, "sdrWhiteLevelNits": 203 },
      "colorDepthBpc": 10,             // applied via vendor layer when available
      "vendorExtras": { }              // reserved: Mosaic/Eyefinity/DRS blobs (P5)
    }
  ],
  "cloneGroups": [],                   // lists of display ids duplicated together
  "audio": { "playbackDeviceId": null, "captureDeviceId": null },  // optional
  "wallpaperRef": null,                // file reference, never embedded
  "replayPayload": { }                 // opaque raw CCD paths/modes — apply-only, never identity
}
```

Matching returns a diff report per display (`Match`, `MatchWithTolerance`, `Mismatch(field)`,
`DisplayMissing`), which drives both "is active" detection and actionable UI ("this profile
needs monitor X which is not connected").

## 5. Apply pipeline (state machine)

```
Resolve identities → patch LUIDs into replay payload
→ [vendor pre-step: Surround/Eyefinity grid, v1.x]         (enable all displays first)
→ SDC_VALIDATE                        → fail: score fallbacks (rebuild clone topology,
                                        topology-only, database clone) or abort with diff
→ SDC_APPLY                           → await WM_DISPLAYCHANGE / timeout (no fixed sleeps)
→ per-source DPI scale (−4)           → verify by re-get
→ per-target HDR state (24H2 or legacy) → verify; InternalRefreshCalibration
→ SDR white levels                    → verify
→ [vendor post-step: color depth / DRS overrides, v1.x]
→ re-capture full state → semantic match against target profile
→ user confirmation window (auto-revert countdown) when invoked interactively
→ optional: taskbar reposition, explorer-restart escape hatch (off by default, per-profile)
```

Every stage: cancellable, logged with structured context, and reports progress. Per-profile
knobs like DisplayMagician's `ApplyProfileCount`/delay are replaced by verification loops with
capped retry budgets; monitor-specific weirdness (Samsung Odyssey G9 double-apply) lives in the
quirk database, not per-profile user settings.

## 6. Feature roadmap

**M0 — Foundation.** Interop layer + identity service + capture to normalized model +
fixture-recording harness. CLI `list`/`capture`/`active` working. Tests green.

**M1 — Profiles MVP (v0.x alpha).** Profile store + migrations; apply pipeline (topology,
mode, rotation, primary, DPI, HDR, SDR white level); tolerant matcher; tray menu + hotkeys +
CLI apply; auto-revert countdown; Velopack packaging.

**M2 — Daily-driver polish (v1.0).** WPF Fluent UI: visual layout editor (physical-mm aware,
LittleBigMouse model), profile gallery with rendered layout icons (Helios's good idea),
settings, toasts; brightness/DDC-CI panel (crash sentinel, quirk DB, WMI internal panels,
SDR-white-level slider under HDR); monitor input switching (VCP 0x60); audio device per
profile; winget + signing.

**M3 — Automation (v1.1).** Trigger/action rules engine (P9); process watch via
WMI/ETW; time-of-day + sunrise/sunset; dock/topology-change triggers; revert-on-exit
semantics; per-app "run with profile" shortcuts (.lnk generation).

**M4 — Vendor depth (v1.2+).** NVIDIA Surround (Mosaic) with the battle-tested teardown
recipe; AMD Eyefinity via ADLX (ADL fallback only if forced); Intel Combined Display; 10-bit /
color-depth control; NVIDIA DRS diff-apply with restore-unset-to-default.

**M5 — QoL expansions (v2 candidates).** Window-layout capture/restore (PersistentWindows
territory), per-profile wallpaper, taskbar management, Auto HDR per-app registry toggles
(experimental flag), virtual display driver integration if detected, ARM64.

## 7. Key risks and mitigations

| Risk | Mitigation |
|---|---|
| Undocumented APIs (DPI −3/−4, SDR white set, InternalRefreshCalibration) shift in a Windows update | Isolated behind interfaces, struct-size tripwires, feature flags, fail-soft with user-visible "unsupported on this build" state; CI canary run on Insider builds |
| NVIDIA Surround instability (worst incumbent bug source) | Deferred past v1.0; when added: validate-first, verified teardown (1×1 grids → EnableCurrentTopo(false)), auto-revert, never on UI thread |
| DDC/CI hardware hostility | P7 machinery (sentinel, quirks DB, timeouts, preclude/preclear) |
| Profile store corruption / model drift | Atomic writes, backups before migration, schema version + tests over fixture stores from every released version |
| Single-dev bus factor (killed Helios) | Small owned interop surface, tests, documented architecture (this file), no closed binaries |

## 8. Research index

| Document | Contents |
|---|---|
| [research/displaymagician-analysis.md](research/displaymagician-analysis.md) | Deep code analysis of DisplayMagician: engine, vendor layers, storage, shortcuts, plumbing, 12 catalogued pain points, reuse-vs-redesign verdicts |
| [research/helios-and-falahati-libs.md](research/helios-and-falahati-libs.md) | HeliosDisplayManagement + WindowsDisplayAPI + NvAPIWrapper: profile model lineage, apply-flow autopsy, NVAPI binding technique |
| [research/auxiliary-projects.md](research/auxiliary-projects.md) | Monitorian, twinkle-tray, HDRTray, AutoActions, LittleBigMouse, SetDPI: identity, DDC/CI, HDR, DPI, automation techniques with file-level citations |
| [research/ecosystem-and-platform.md](research/ecosystem-and-platform.md) | Incumbent bug taxonomy with issue links, competitor matrix, Windows 11 API verification, UI-stack evaluation, distribution/signing plan |

Reference clones live in [`reference/`](../reference/) (gitignored).
