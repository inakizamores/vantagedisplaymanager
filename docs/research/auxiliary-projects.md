# Auxiliary Projects Survey

Research notes for Vantage: Display Manager, based on reading the source of six auxiliary
projects cloned under `reference\`. Companion to the survey of the four primary projects
(DisplayMagician, HeliosDisplayManagement, WindowsDisplayAPI, NvAPIWrapper).

All paths below are relative to `C:\Users\inaki\Documents\GitHub\vantagedisplaymanager\reference\`.

---

## 1. Monitorian — DDC/CI brightness done right (C#/WPF)

**What it is.** Per-monitor brightness/contrast tray app by emoacht. C#, WPF, .NET Framework 4.8
(`Monitorian\Source\Monitorian\Monitorian.csproj`). Split into `Monitorian` (UI),
`Monitorian.Core` (all monitor logic), `ScreenFrame`, `StartupAgency`.

**Brightness APIs.** `Monitorian\Source\Monitorian.Core\Models\Monitor\MonitorConfiguration.cs`:

- External monitors via **Dxva2.dll**: `GetNumberOfPhysicalMonitorsFromHMONITOR`,
  `GetPhysicalMonitorsFromHMONITOR`, `GetMonitorCapabilities`, `GetMonitorBrightness` /
  `SetMonitorBrightness` (high-level), and `GetVCPFeatureAndVCPFeatureReply` / `SetVCPFeature`
  (low-level) with VCP codes: Luminance `0x10`, Contrast `0x12`, Temperature `0x14`,
  InputSource `0x60`, SpeakerVolume `0x62`, PowerMode `0xD6`. Capabilities are probed with
  `CapabilitiesRequestAndCapabilitiesReply` (MCCS capabilities string) plus the `MC_CAPS` flags.
- Internal panels via **WMI** (`Models\Monitor\MSMonitor.cs`): `Win32_DesktopMonitor` for
  identification, `root\wmi` classes `WmiMonitorBrightness` (read + supported `Level[]` steps),
  `WmiMonitorBrightnessMethods.WmiSetBrightness(timeout, brightness)` for writes, `WmiMonitorID`
  for EDID product code / serial, and a `ManagementEventWatcher` on `WmiMonitorBrightnessEvent`
  so hardware hotkey changes are reflected in the UI (`StartBrightnessEventWatcher`).

**Enumeration & matching** (`Models\Monitor\MonitorManager.cs`). Four sources are joined:

1. `DeviceContext.EnumerateMonitorDevices()` — `EnumDisplayDevices` (per display, then per
   monitor) yielding device instance ID, description, `DisplayIndex`/`MonitorIndex`.
2. `DisplayConfig.EnumerateDisplayConfigs()` — `QueryDisplayConfig` +
   `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`; converts `monitorDevicePath` to a device
   instance ID (`DeviceConversion.cs`) and keeps a `DisplayIdSet(adapterId LUID, target id)`.
3. WinRT `Windows.Devices.Display.DisplayMonitor.FromInterfaceIdAsync`
   (`DisplayMonitorProvider.cs`) for the friendly name and `ConnectionKind`
   (Internal vs wired) — with a `FileNotFoundException` fallback for pre-1803 Windows.
4. `Dxva2` physical monitor handles from `EnumDisplayMonitors` HMONITORs.

   The join key is the **device instance ID** (case-insensitive), then physical handles are
   matched by `DisplayIndex` + `MonitorIndex` + description equality. Physical-monitor probing
   runs in parallel `Task.Run` per HMONITOR with an overall timeout so one dead monitor cannot
   hang startup.

**Flaky-DDC handling.**

- `GetVcpValue`/`SetVcpValue` retry once when the error is transient
  (`CheckPossibleTransientStatus`: `ERROR_GRAPHICS_DDCCI_INVALID_*` "DdcMessageInvalid" or
  "TransmissionFailed"; constants at `MonitorConfiguration.cs:676`).
- Every access returns an `AccessResult` with a parsed status instead of a bare bool; the
  comment at `SetBrightness` notes `SetMonitorBrightness` can return true even on failure.
- Command-line options `/preclude <id>` (blacklist a monitor) and `/preclear <id>` (force-treat
  a monitor as DDC-capable when its capabilities string lies) — `MonitorManager.cs:84-100`.
- Raw min/max brightness are respected (not assumed 0–100) and percentages converted.

**Robustness / hot-plug** (`Monitorian.Core\Models\Watcher\`):

- `DisplaySettingsWatcher` — `SystemEvents.DisplaySettingsChanged`, then a `TimerWatcher`
  fires *repeated* rescans on a count schedule (display changes settle asynchronously).
- `PowerWatcher` — `SystemEvents.PowerModeChanged` plus power setting notifications; resume
  triggers rescans at intervals of 5, 5, 10, 10, 30 seconds because monitors reappear late.
- `SessionWatcher` (lock/unlock), `BrightnessWatcher` (WMI event), `SystemEventsComplement`
  (RegisterPowerSettingNotification, e.g. console display state).
- `SafePhysicalMonitorHandle` (SafeHandle) everywhere; handles disposed when unmatched.

**Takeaways for Vantage**

1. Copy the four-source enumeration join keyed on device instance ID; it is the most complete
   monitor-identity solution of any project surveyed, and `DisplayIdSet` (LUID+target id) is
   exactly what the CCD APIs need later.
2. Use high-level `Get/SetMonitorBrightness` first, drop to VCP 0x10 when unsupported, and keep
   Monitorian's transient-error single-retry — not endless retry loops.
3. Watch `WmiMonitorBrightnessEvent` so the UI tracks changes made by monitor buttons/OSD.
4. Rescan on a decaying timer after resume/display-change, never just once.
5. Provide escape hatches equivalent to `/preclude` and `/preclear` for broken monitors.

---

## 2. twinkle-tray — feature-rich brightness UX (Electron/Node)

**What it is.** Brightness manager by Xander Frangos. Electron 43 + React 18, Parcel bundler
(`twinkle-tray\package.json`). Heavy lifting is done by vendored native N-API addons in
`twinkle-tray\src\modules\`:

- `node-ddcci` (`@hensm/ddcci` fork, `modules\node-ddcci\ddcci.cc`) — C++ addon wrapping the
  same Dxva2 calls as Monitorian (`GetPhysicalMonitorsFromHMONITOR`,
  `CapabilitiesRequestAndCapabilitiesReply`, `GetVCPFeatureAndVCPFeatureReply`, `SetVCPFeature`,
  `Get/SetMonitorBrightness`).
- `win32-displayconfig` — `QueryDisplayConfig`/`DisplayConfigGetDeviceInfo` wrapper providing
  device path, output technology, scaling, source mode per display.
- `windows-hdr` (`modules\windows-hdr\windows-hdr.cc`) — gets/sets **SDR white level** via
  `DisplayConfigSetDeviceInfo` (`SDRWhiteLevel = nits * 1000 / 80`), used as the "brightness"
  control when a display runs HDR ("SDR content brightness" slider).
- `wmi-bridge` — native WMI access (internal panel brightness) avoiding slow `wmic`/PowerShell.
- `windows-ambient-sensor` (WinRT ALS), `@paymoapp/active-window`, `global-mouse-events`
  (scroll over tray icon = brightness), `studio-display-control` (Apple Studio Display over USB!).

**Monitor identity.** `src\Monitors.js:1103` (`getMonitorsWin32`): splits the CCD
`devicePath` on `#` → `hwid[1]` = model code (e.g. `DEL41D9`), `hwid[2]` = the unique
instance/UID segment (trimmed at `_`), which becomes the persistent settings key. WMI and Win32
results are merged per `hwid[2]` (`updateDisplay`). `monitor-rules.json` ships per-model quirk
rules keyed on `hwid[1]` (`ddcBrightnessCodes` — nonstandard brightness VCP codes like 107 for
some Fujitsu panels; `skipReapply` for monitors that misbehave on brightness reapply).

**Flaky-DDC handling — the standout idea.** `src\Monitors.js:160-256`: a **crash sentinel
file**. Before any risky DDC probe (`withDDCSentinel`) it writes `ddc-probe.lock` with the
stage + monitor id; a native crash never deletes it, so the next launch finds "crash evidence"
(`checkForDDCCrashEvidence`), records the offending monitor in `ddc-unstable.json`, and
downgrades DDC validation from "accurate" (read-back verification) to "fast" for that display.
Feature probing also has per-monitor timeouts and cached feature snapshots
(`saveFeatureSnapshot`) so full capability scans aren't repeated every refresh.

**Feature set worth copying.**

- Per-monitor sliders + optional linked levels, drag-to-reorder, rename, hide.
- Hotkeys (`electron.js:1467`, `globalShortcut`) with multi-action support (per-monitor or all,
  offsets, cycle presets, "turn off displays") and an on-screen overlay while adjusting.
- **Time-of-day adjustments** (`adjustmentTimes`) including sunrise/sunset via `suncalc` with
  user lat/long (`adjustmentTimeLatitude/Longitude`), optional per-display levels and animated
  transitions (`adjustmentTimeAnimate`, `adjustmentTimeSpeed`).
- Ambient light sensor auto-brightness (WinRT sensor or Yoctopuce USB lux meter).
- Idle dimming with restore (`idleRestoreSeconds`), "hide closed lid display" logic.
- Windows-accent-colored acrylic flyout that mimics the Win11 quick-settings panel.

**Robustness / hot-plug.** Monitor work runs in a separate hidden renderer ("monitor thread")
that can be killed/restarted. Display churn: Electron `screen` events `display-added` /
`display-removed` / `display-metrics-changed` (`electron.js:4688-4690`). Sleep/wake:
`powerMonitor.on("resume")` (`electron.js:4791`) sets a `resumeRecoveryInProgress` flag,
temporarily blocks known-bad displays, restarts the monitor thread if needed, coalesces the
event storm Windows emits during resume, and re-applies time-of-day adjustments; `suspend`,
`lock-screen`/`unlock-screen` handled similarly.

**Takeaways for Vantage**

1. Adopt the crash-sentinel + per-monitor quirk database pattern; it converts "DDC hangs/crashes
   the app for some users" into a self-healing degradation. Ship a `monitor-rules.json`
   equivalent that can be updated independently of releases.
2. When HDR is on, brightness must control **SDR white level**, not VCP 0x10 — twinkle-tray and
   Monitorian (HdrMonitorItem) agree on this; slider semantics switch per-display.
3. Time-of-day + sunrise/sunset automation and hotkey actions are the most-loved UX features;
   both map cleanly onto Vantage's profile engine.
4. Persist settings against the device-path UID segment, with model-code fallback matching,
   so settings survive port swaps.
5. Avoid the Electron cost: twinkle-tray needs 6+ custom native addons to reach APIs a .NET/C++
   app gets directly. Its architecture is a strong argument for Vantage staying native.

---

## 3. HDRTray — minimal, correct HDR toggling (C++)

**What it is.** Tray icon + CLI (`HDRCmd`) to toggle Windows HDR, by res2k. Pure Win32 C++20,
CMake, no framework (`HDRTray\CMakeLists.txt`); requires Windows SDK ≥ 10.0.26100
(`common\HDR.cpp:19`). Subcommands `enable/disable/status/toggle` in `HDRCmd\subcommand\`.

**The HDR API — both generations** (`HDRTray\common\HDR.cpp`):

- Enumerate: `GetDisplayConfigBufferSizes` + `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`,
  operating on `path.targetInfo` (adapterId + target id).
- **Detect** — prefers Windows 11 24H2 `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2`:
  checks `highDynamicRangeSupported` and treats only
  `activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR` as "HDR on" (crucial: with ACM
  enabled, the old "advanced color enabled" bit is always set for SDR too). Falls back to
  `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` (`advancedColorSupported/Enabled`) on
  older builds, gated by an `IsWindows11_24H2OrGreater()` runtime check.
- **Set** — tries 24H2 `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE`
  (`DISPLAYCONFIG_SET_HDR_STATE.enableHdr`) first, falls back to
  `DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE`
  (`DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE.enableAdvancedColor`) via
  `DisplayConfigSetDeviceInfo`. It then **re-queries** status rather than trusting the return.
- After toggling it calls undocumented `InternalRefreshCalibration(nullptr, 0, nullptr,
  nullptr)` (exported from `msmcs`) so Windows reloads color calibration (`HDR.cpp:152`).
- **SDR white level**: undocumented `DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL = 0xFFFFFFEE`
  with `SDRWhiteLevel = nits * 1000 / 80` (`HDR.cpp:213-237`); the tray menu offers presets
  (80 nits sRGB, 203 nits BT.2408 — `HDRTray\HDRTray.cpp:195-200`).
- Display naming: `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`, with
  `flags.friendlyNameFromEdid` check and a `GET_TARGET_BASE_TYPE` fallback for internal panels.

**Robustness.** `HDRTray\HDRTray\HDRTray.cpp:209-218`: on `WM_DISPLAYCHANGE` it updates status,
and because HDR state is **not always up-to-date at WM_DISPLAYCHANGE time**, it re-checks on a
timer (10 × 500 ms). It also re-adds the tray icon on the `TaskbarCreated` broadcast message
(Explorer restarts) and handles dark mode via `WM_SETTINGCHANGE`.

**Takeaways for Vantage**

1. Use exactly this dual-path HDR strategy: 24H2 `SET_HDR_STATE`/`GET_ADVANCED_COLOR_INFO_2`
   first, legacy advanced-color otherwise. Never interpret `advancedColorEnabled` as "HDR on"
   on 24H2+ systems with ACM.
2. Always re-query state after a set; both HDRTray and Monitorian treat setters as unreliable.
3. Re-check HDR/display state on a short timer after `WM_DISPLAYCHANGE` — the OS lies briefly.
4. SDR white level presets (80/203 nits) are cheap to add and genuinely useful; keep the
   `nits*1000/80` conversion.
5. Ship a CLI twin of the GUI (HDRCmd model): profile automation and power users get scripting
   for free.

---

## 4. AutoActions — profile automation on app launch (C#/WPF)

**What it is.** Formerly "HDRProfile": watches for configured applications and fires profiles
of actions (HDR on, resolution/refresh change, audio device switch, run/close program) when
they start, get focus, lose focus, or close. C#/WPF, .NET Framework 4.8
(`AutoActions\Source\AutoActions\AutoActions.csproj`), plus a native C++ `HDRController.dll`
and the bundled `NvAPIWrapper`.

**Process/game detection** (`Source\AutoActions\ProcessWatcher.cs`): a background thread
**polls** `Process.GetProcesses()` every `Globals.GlobalRefreshInterval` and diffs against the
user's application list; focus is detected via `GetForegroundWindow()`
(`WinAPIFunctions.cs:14`). UWP apps get special handling (`UWP\UWPAppsManager.cs`,
`WWAHostHandler.cs` resolves apps hosted in `WWAHost`). No ETW/WMI process events — plain
polling. State transitions raise `ApplicationChangedType` {Started, Closed, GotFocus,
LostFocus} consumed by `AutoActionsDaemon.UpdateCurrentProfile` (`AutoActionsDaemon.cs:222`),
which runs the profile's entry/exit action lists.

**Actions** (`Source\AutoActions\Profiles\Actions\`): `DisplayAction` (HDR on/off, resolution,
refresh rate, color depth — per display or all), `AudioDeviceAction` (default playback/capture
via a bundled CoreAudio AudioSwitcher fork in `AutoActions.Audio`), `RunProgramAction`,
`CloseProgramAction`, `ReferenceProfileAction` (profile composition).

**Display control** (`Source\AutoActions.Displays\`):

- HDR via native `HDRController.dll` (`HDRController\HDRController\HDRController.cpp`) — same
  24H2-first pattern as HDRTray: `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE` (line 194 ff.) with
  `SET_ADVANCED_COLOR_STATE` fallback, `GET_ADVANCED_COLOR_INFO_2` then `GET_ADVANCED_COLOR_INFO`
  for status; per-display or global. Displays are addressed by **UID = CCD `targetInfo.id`**
  (`_GetUID`, line 139), which is stable per session but not a serial.
- Resolution/refresh via `ChangeDisplaySettingsEx` (`DisplayInterop.cs:10`,
  `DisplayManagerBase.SetResolution/SetRefreshRate`).
- On NVIDIA, `DisplayManagerNvidia.cs` uses **NvAPIWrapper** (`ColorDataHDRMode.UHDA`,
  `ColorDataDepth`) for HDR + 10-bit color depth — the vendor API can set bit depth, which the
  public Windows API cannot.

**Robustness.** The daemon restarts profiles on app restart (`RestartApplication`), has a
`NightLightManager`, tray-only UI, and elevated-startup support. Monitor identity is the weak
point: session-scoped target IDs, no EDID serial matching.

**Takeaways for Vantage**

1. The trigger model (app started/focused/lost focus/closed → enter/exit action lists) is a
   proven UX for "HDR on when game launches"; adopt it but implement detection with WMI process
   events (`Win32_ProcessStartTrace`) or ETW instead of polling, and keep `GetForegroundWindow`
   polling only for focus.
2. Treat AutoActions' action taxonomy (display settings, audio device, run/close program,
   profile reference) as the baseline action set for Vantage's automation engine.
3. Don't copy its display identity: addressing monitors by raw CCD target id breaks across
   reboots/re-plugs. Combine AutoActions' trigger engine with Monitorian-style instance IDs.
4. Vendor APIs (NvAPI) are required for color depth / dithering-class settings; plan an
   optional NVIDIA/AMD layer rather than pretending CCD covers everything.
5. UWP titles need explicit support (package family names, WWAHost) or game detection will
   miss Game Pass titles.

---

## 5. LittleBigMouse — physical-space mouse transitions (C# + Rust)

**What it is.** Aligns multi-DPI monitors in *physical millimeter space* so the cursor crosses
screen borders where the glass actually meets, by mgth. Two halves: an **Avalonia** UI/layout
editor (`LittleBigMouse\LittleBigMouse.Ui\LittleBigMouse.Ui.Avalonia`, .NET) and a **Rust
daemon** (`LittleBigMouse\LittleBigMouse-Hook-Rust\`) that does the actual hooking (a rewrite
of the older C++ daemon).

**Physical layout model** (`LittleBigMouse.Core\LittleBigMouse.DisplayLayout\`):

- `Monitors\PhysicalMonitor.cs` models each screen in mm: physical size from EDID, rotated
  (`PhysicalRotated`), scaled by user ratio, and located in a shared mm plane (reactive
  pipeline, lines 115-160). `Dimensions\` contains the algebra (DisplayRect/Size/Scale/Rotate,
  `DisplaySizeInMm`, `DisplayScaleDip`).
- EDID comes from the registry: `HLab.Sys\HLab.Sys.Windows.Monitors\Factory\
  MonitorDeviceHelper.cs:569-613` — `SetupDiGetClassDevsEx` + `SetupDiOpenDevRegKey` →
  `Device Parameters\EDID` blob, parsed by `HLab.Sys\HLab.Sys.Monitors.Edid\Edid.cs`
  (manufacturer 3-letter code from bytes 8-9, product code bytes 10-11, serial, descriptor-block
  serial number `Block(0xFF)`, physical size mm from bytes 21/22 with detailed-timing fallback
  bytes 66-68). Monitors without EDID get a synthetic `NOEDID_{pnpCode}_{deviceId}` identity.
- DPI per monitor via `GetDpiForMonitor` (shcore) (`Dimensions\DisplayScaleDip.cs`,
  `Monitors\DisplaySource.cs`), so the model knows effective vs raw DPI per screen.

**Hook technique** (`LittleBigMouse-Hook-Rust\src\`):

- `hook\windows\mod.rs:156`: `SetWindowsHookExW(WH_MOUSE_LL, mouse_proc, ...)` low-level mouse
  hook on a dedicated thread with a `GetMessageW` pump; the comment at line 59 warns that a
  hook callback exceeding the OS `LowLevelHooksTimeout` (~300 ms) gets silently removed —
  so the callback only posts events to the engine thread.
- The engine (`engine\`, `zones\`) keeps per-monitor `Zone`s that map pixels ↔ mm linearly
  (`zones\zone.rs:94 to_pixels`, `contains_mm`); when the cursor hits a border it converts to
  mm, finds the zone reachable in physical space, and warps with `SetCursorPos`
  (`platform\windows\cursor.rs:39`), using `ClipCursor` for containment. `zones\travel.rs`
  ports the C++ `Reachable`/`Travel` algorithm computing the chain of clip rects needed to
  move between non-adjacent pixel rects without escaping the desktop.
- **IPC**: daemon exposes a named pipe (Unix socket on Linux) carrying length-prefixed XML
  (`ipc\server.rs`), validates the client is in the same session via
  `GetNamedPipeClientProcessId` (line 325). The UI serializes the layout to XML; the daemon is
  UI-free and can run at login.

**Robustness.** `HLab.Sys\HLab.Sys.Windows.Monitors\DisplayChangeMonitor.cs` listens for
`WM_DISPLAYCHANGE` to re-enumerate. Monitor identity is EDID-based (manufacturer+product+serial),
the strongest cross-session identity in the surveyed set. The Rust daemon survives UI exit.

**Takeaways for Vantage**

1. Steal the EDID registry-read path (`SetupDiOpenDevRegKey` → `EDID` value) and the
   `NOEDID_*` fallback naming for cross-session monitor identity in profiles.
2. A physical-mm layout model (EDID size + rotation + per-monitor DPI) is the right foundation
   for any layout-editor UI Vantage ships, even if Vantage never hooks the mouse.
3. If Vantage adds cursor QoL features, use WH_MOUSE_LL with a do-nothing callback that posts
   to a worker — respect the 300 ms hook timeout or Windows unhooks you silently.
4. The split of privileged/always-running daemon + optional UI over a named pipe is a good
   architecture template for Vantage's background service; session-validating pipe clients is
   a security detail worth copying.
5. Avalonia here (and WPF elsewhere) shows .NET desktop UI is viable for complex layout
   editors; the perf-critical part (hook) lives in Rust — keep hot paths out of the UI process.

---

## 6. SetDPI — per-monitor display scaling CLI (C++)

**What it is.** Tiny console tool (3 source files: `SetDPI\SetDpi.cpp`, `DpiHelper.cpp`,
`DpiHelper.h`) that gets/sets Windows per-monitor DPI scaling. Visual C++, no dependencies.

**The undocumented mechanism** (`SetDPI\DpiHelper.h/.cpp`) — reverse-engineered from the
Settings app:

- Two private `DisplayConfigGetDeviceInfo`/`SetDeviceInfo` packet types:
  `DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3` and `..._SET_DPI_SCALE = -4`
  (negative values outside the public enum, `DpiHelper.h:35-39`).
- GET returns `minScaleRel / curScaleRel / maxScaleRel` — offsets **relative to the
  OS-recommended scale** — which are converted to absolute percentages by indexing the fixed
  table `DpiVals[] = {100,125,150,175,200,225,250,300,350,400,450,500}` (`DpiHelper.h:13`;
  e.g. `minScaleRel == -3` ⇒ recommended is 175%).
- SET writes a single `scaleRel` int (steps from recommended) in a
  `DISPLAYCONFIG_SOURCE_DPI_SCALE_SET` packet. Both packets carry `assert(sizeof == 0x20/0x18)`
  tripwires in case the OS struct layout changes (`DpiHelper.cpp:55,142`).
- **Scaling is a property of the *source*, not the target**: the header takes
  `adapterId` + `sourceID` from `QueryDisplayConfig` paths (`SetDpi.cpp:40-85` enumerates
  active paths, resolves friendly names via `GET_TARGET_NAME`, flags
  `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` panels).
- The recommended value can also be read via
  `SystemParametersInfo(SPI_GETLOGICALDPIOVERRIDE)` (`SetDpi.cpp:17`).
- The change applies live — no logoff, no broadcast needed; Windows persists it per monitor.

**Robustness.** None to speak of (one-shot CLI, monitor addressed by enumeration index), which
is fine for its scope but means scripts break when the index order changes.

**Takeaways for Vantage**

1. This is *the* mechanism for putting per-monitor scaling into display profiles — nothing
   public does it. Wrap GET/SET `-3/-4` with strict struct-size checks and graceful failure,
   since it's undocumented and could shift in a future Windows release.
2. Remember scaling rides the **source** (adapterId+sourceId), while HDR/DPI-name/EDID ride the
   **target** — Vantage's profile model must store both halves of each CCD path.
3. Clamp to the reported `min/maxScaleRel` and snap to the `DpiVals` table; arbitrary values
   are not representable.
4. Apply scaling *after* resolution changes when restoring a profile — the relative offsets are
   computed against the recommended scale, which depends on the active mode.

---

## Cross-project takeaways

**Monitor identity (the recurring hard problem).** Every project solves it differently:
Monitorian joins on device instance ID; twinkle-tray keys settings on a device-path segment +
model code; LittleBigMouse uses parsed EDID (vendor/product/serial); AutoActions uses raw CCD
target ids (worst). Vantage should build one identity service: device instance ID as the
primary key, EDID serial (via `WmiMonitorID` or the SetupAPI registry blob) for cross-session
profile matching, and `DisplayIdSet(adapterLUID, sourceId, targetId)` resolved fresh each
session for CCD calls.

**One CCD core, many features.** HDR toggle (HDRTray/AutoActions), SDR white level
(twinkle-tray/Monitorian/HDRTray), DPI scaling (SetDPI), target names, and topology all flow
through `QueryDisplayConfig` + `DisplayConfigGet/SetDeviceInfo`. Vantage should own a single
well-tested CCD wrapper (documented + undocumented packet types: `SET_HDR_STATE`,
`GET_ADVANCED_COLOR_INFO_2`, `SET_SDR_WHITE_LEVEL 0xFFFFFFEE`, DPI `-3/-4`) with runtime
Windows-version gating exactly like HDRTray's `use_win11_24h2_color_functions`.

**Treat the OS as eventually consistent.** Verified setters (re-query after set), repeated
rescans after `WM_DISPLAYCHANGE`/resume on decaying timers (Monitorian 5/5/10/10/30 s, HDRTray
10×500 ms), and coalescing of resume event storms (twinkle-tray) appear independently in three
codebases — that is the pattern, not an implementation detail.

**DDC/CI is hostile territory.** Budget for: capability strings that lie (preclear),
monitors that hang or crash the process mid-probe (crash sentinel + per-monitor timeouts +
parallel probing), nonstandard VCP codes (model quirk rules), non-0-100 raw ranges, and
hardware-side changes (WMI brightness events). Probe capabilities once and cache per monitor.

**Automation engine shape.** AutoActions' triggers (process lifecycle) + twinkle-tray's
triggers (time of day, sun position, idle, ambient light, resume) + a shared action list
(display settings, HDR, brightness, audio, run program) compose naturally into one rules
engine over Vantage's profile store. Ship a CLI (HDRCmd-style) so every action is scriptable.

**Stack validation.** Four of six are C#/.NET (WPF ×3, Avalonia ×1) and reach every API needed
via P/Invoke; the Electron app needed six custom native addons; the two C++ projects are thin.
A native .NET 8+ app with a small P/Invoke layer (or a Rust/C++ helper process for hooks, per
LittleBigMouse) is the well-trodden path.
