# Helios Display Management & the falahati Libraries — Deep Code Analysis

Research for **Vantage: Display Manager**. Sources analyzed (local clones under `reference/`):

| Repo | Last commit | Last *code* activity | Status |
|---|---|---|---|
| `reference/HeliosDisplayManagement` | 2021-03-27 (README only) | ~Nov 2020 | Abandoned; superseded by the DisplayMagician fork |
| `reference/WindowsDisplayAPI` | 2023-01-22 (external PR: handle finalizer fix) | v1.3.0.13 | Effectively unmaintained since ~2020 |
| `reference/NvAPIWrapper` | 2020-12-09 ("new release", v0.8.1.101) | Targets NVAPI **R410** (2018 SDK) | Unmaintained |

---

## 1. HeliosDisplayManagement

### 1.1 Solution structure, frameworks, dependencies

`reference/HeliosDisplayManagement/HeliosDisplayManagement.sln` contains four projects (old-style non-SDK csproj, **.NET Framework 4.5**, except Reporting at 4.6.2):

- **HeliosDisplayManagement** — WinForms exe (`Program.cs`, `UIForms/MainForm.cs`, `EditForm.cs`, `ShortcutForm.cs`, `SteamGamesForm.cs`, `SplashForm.cs`).
- **HeliosDisplayManagement.Shared** — the actual domain layer: `Profile.cs`, `Topology/Path.cs`, `Topology/PathTarget.cs`, `NVIDIA/SurroundTopology.cs`, `ProfileIcon.cs`, `TaskBarStuckRectangle.cs`.
- **HeliosDisplayManagement.ShellExtension** — SharpShell COM context-menu handlers (`HeliosDesktopMenuExtension.cs`, `HeliosExecutableMenuExtension.cs`, `HeliosSteamUrlMenuExtension.cs`).
- **HeliosDisplayManagement.Reporting** — console diagnostic dumper of all WindowsDisplayAPI/NvAPIWrapper state.

Dependencies (from `HeliosDisplayManagement/HeliosDisplayManagement.csproj`): `WindowsDisplayAPI 1.2.0.2`, `NvAPIWrapper.Net` (via Shared), `Newtonsoft.Json 11`, **CommandLineParser 1.9.71** (2015-era), `HtmlAgilityPack` (for scraping steamdb.info), `IconLib`, `CircularProgressBar`, `WinFormAnimation`, **WCF (`System.ServiceModel`)** for single-instance IPC, `SharpShell` for the shell extension. UI framework: **WinForms** throughout — even the Shared "model" project references `System.Windows.Forms`.

### 1.2 Profile model, capture, and apply

**Model** (all JSON-serialized with Newtonsoft):

- `Shared/Profile.cs` — `Profile { Id (GUID string), Name, Path[] Paths }`. Persisted as an array to `%AppData%\HeliosDisplayManagement\DisplayProfiles_2.0.json` (`Profile.ProfilesPath`, versioned only via filename). Serialization uses `TypeNameHandling.Auto` — a known deserialization attack vector.
- `Shared/Topology/Path.cs` — one CCD path source: `SourceId`, `PixelFormat` (`DisplayConfigPixelFormat`), `Position` (Point), `Resolution` (Size), `PathTarget[] Targets` (>1 target = clone group).
- `Shared/Topology/PathTarget.cs` — one CCD path target: `DevicePath` (PnP device instance path, deliberately truncated at the first `{` GUID so it survives GUID churn), `DisplayName`, `FrequencyInMillihertz`, `Rotation`, `Scaling`, `ScanLineOrdering` (own enums in `Shared/Rotation.cs` etc., mapped via `Topology/PathHelper.cs`), plus an optional `SurroundTopology`.
- `Shared/NVIDIA/SurroundTopology.cs` / `SurroundTopologyDisplay.cs` — NVIDIA Mosaic grid: `Rows`, `Columns`, `Resolution`, `ColorDepth`, `Frequency`, `Displays[]`, and flags (`ApplyWithBezelCorrectedResolution`, `ImmersiveGaming`, `BaseMosaicPanoramic`, `DriverReloadAllowed`, `AcceleratePrimaryDisplay`).

Identity/equality is deep and order-insensitive: `Profile.Equals` compares path sets; `IsActive` is computed by capturing the *current* config and comparing it to the saved one — a clever "no state, just compare" design.

**Capture**: `Profile.GetCurrent()` → `WindowsDisplayAPI.DisplayConfig.PathInfo.GetActivePaths()` (CCD `QueryDisplayConfig`) → wrapped into `Path`/`PathTarget`.

**Surround detection** (`SurroundTopology.FromPathTargetInfo`) is heuristic: a target is considered an NVIDIA Surround virtual display if its EDID manufacture code is `"NVS"`, OR friendly name is `"NV Surround"`, OR its device path contains `"&UID5120"`. It then cross-correlates the Windows CCD path with `NvAPIWrapper.Display.PathInfo.GetDisplaysConfig()` **by matching position + resolution**, takes the first NvAPI target, and finds the `GridTopology` containing that display. Three chained heuristics, each a potential false match — this is the fragile heart of the app.

**Apply** (`Profile.Apply()`, `Shared/Profile.cs:264-325`) — the most instructive (and worst) code:
1. `Thread.Sleep(2000)` (hardcoded).
2. If profile has surround topologies → `GridTopology.SetGridTopologies(..., MaximizePerformance)` (NvAPI Mosaic). If the profile has none but the *current* config does → tears surround down by re-applying each display as a 1×1 grid.
3. `Thread.Sleep(18000)` — waits blindly 18 s for the driver to settle.
4. `Path.ToPathInfo()` re-resolves each saved `DevicePath` against live `PathDisplayTarget.GetDisplayTargets()` (prefix match), then `PathInfo.ApplyPathInfos(pathInfos, true, true, true)` → CCD `SetDisplayConfig`.
5. `Thread.Sleep(10000)`; on exception: `MessageBox.Show` **inside the model class**.

That's ~30 s of fixed sleeps per switch and zero event-driven confirmation. Failures are silently swallowed (`catch { /* ignored */ }` appears dozens of times across the codebase).

### 1.3 Steam/game integration

- `HeliosDisplayManagement/Steam/SteamGame.cs`: ownership/installed/running/updating state read from registry `HKCU\SOFTWARE\Valve\Steam\Apps\<appId>` (`Installed`, `Running`, `Updating` values); Steam exe path from `HKCU\SOFTWARE\Valve\Steam\SteamExe`. Game names are fetched by **scraping `https://steamdb.info/api/GetAppList/`** with a spoofed Firefox UA and **TLS certificate validation disabled globally** (`ServerCertificateValidationCallback += (…) => true` in the static ctor). Icons are scraped from steamdb.info HTML with HtmlAgilityPack. Cached to `%AppData%\...\SteamGames.json` / `SteamIconCache`.
- Launch flow (`Program.SwitchProfile`, `Program.cs:159-388`): switch profile → start `steam://rungameid/<appId>` (or a plain exe via `-e`) → poll registry/process every 300 ms until running (with `-t` timeout, paused while `IsUpdating`) → tray `NotifyIcon` "waiting" → on exit, **roll back to the pre-switch profile** captured at the start.
- `ShortcutForm.cs` creates desktop `.lnk` shortcuts embedding the full CLI (profile id + game/exe), with per-profile icons rendered by `Shared/ProfileIcon.cs` (draws the monitor layout into a `MultiIcon`).
- `ShellExtension/HeliosSteamUrlMenuExtension.cs` etc. add an "Open under Display Profile" right-click menu on the desktop, on `.exe` files, and on Steam `.url` shortcuts (parses the appid out of the URL file).

### 1.4 CLI / silent switching

`HeliosDisplayManagement/CommandLineOptions.cs`: `-a|--action` (`None|SwitchProfile|EditProfile|CreateShortcut` from `Shared/HeliosStartupAction.cs`), `-p` profile id, `-e` executable, `--arguments`, `-w` wait-for process name, `-t` timeout (default 30 s), `-s` Steam AppId. So `HeliosDisplayManagement -a SwitchProfile -p {guid}` is the silent-switch path (though "silent" still shows a `SplashForm` progress dialog and `MessageBox` on error — there is no true headless mode). Single-instancing/busy-state coordination is done over **WCF named pipes** (`InterProcess/IPCService.cs`, `IPCClient.QueryAll()` enumerates other instances to refuse concurrent switches).

### 1.5 Age-related and architectural issues

- .NET Framework 4.5 (EOL), WinForms-only, WCF (does not exist on modern .NET), CommandLineParser 1.x API.
- Blocking `Thread.Sleep`-based orchestration; UI concerns (`MessageBox`, `SplashForm`) welded into domain classes; `Profile` has a static ctor that initializes NVAPI as a side effect.
- Swallow-everything exception policy → undiagnosable failures.
- `TypeNameHandling.Auto` JSON; UTF-16 (`Encoding.Unicode`) files; no schema migration.
- steamdb scraping (breaks ToS, brittle) + disabled TLS validation.
- NVIDIA-only multi-GPU story: no AMD Eyefinity, no Intel; no HDR, no DPI, no wallpaper/audio per profile.
- The successor fork **DisplayMagician** (also in `reference/DisplayMagician`) is the proof of the verdict: `DisplayMagicianShared` dropped both falahati libraries entirely and re-implemented raw native layers per vendor (`DisplayMagicianShared/NVIDIA`, `AMD`, `Intel`, `Windows` folders).

---

## 2. WindowsDisplayAPI

Single library project `reference/WindowsDisplayAPI/WindowsDisplayAPI/WindowsDisplayAPI.csproj` (SDK-style, `netstandard2.0;net45`), plus `WindowsDisplaySample`. Two P/Invoke surfaces, both thin static classes:

- `Native/DeviceContextApi.cs` — user32/gdi32: `EnumDisplayDevices`, `EnumDisplaySettings(Ex)`, `ChangeDisplaySettingsEx`, `GetMonitorInfo`/`MonitorFromWindow/Point`, `Get/SetDeviceGammaRamp`, `GetDeviceCaps`.
- `Native/DisplayConfigApi.cs` — user32 CCD: `GetDisplayConfigBufferSizes`, `QueryDisplayConfig`, `SetDisplayConfig`, and overloaded `DisplayConfigGetDeviceInfo`/`SetDeviceInfo` per request struct (a nice trick: one overload per `ref` struct type instead of `IntPtr` casting).

### 2.1 Class → native API map

| Public class | Underlying native API |
|---|---|
| `DisplayAdapter` (`DisplayAdapter.cs`) | `EnumDisplayDevices(null, i, …)` — adapter enumeration |
| `Display` / `UnAttachedDisplay` (`Display.cs`, `UnAttachedDisplay.cs`, base `DisplayDevice.cs`) | `EnumDisplayDevices(adapterName, j, …)` second level; attached vs detached state flags; `GammaRamp` via gdi32 `Get/SetDeviceGammaRamp`; capabilities via `GetDeviceCaps` |
| `DisplayScreen` (`DisplayScreen.cs`) | `MonitorInfo` + `Enable/Disable/SetSettings` via `ChangeDisplaySettingsEx`; `FromPoint/FromWindow` via `MonitorFrom*` |
| `DisplaySetting` / `DisplayPossibleSetting` (`DisplaySetting.cs`) | `DEVMODE` (`EnumDisplaySettings`, `ChangeDisplaySettingsEx`) — resolution, position, frequency, color depth, orientation, fixed-output scaling; two-phase batch apply (`NoReset` per display then global `Reset`, see `SaveDisplaySettings`) |
| `DisplayConfig.PathInfo` (`DisplayConfig/PathInfo.cs`) | `DISPLAYCONFIG_PATH_INFO` + source mode; `GetActivePaths()`, `GetAllPaths()`, `ApplyPathInfos()`, `ValidatePathInfos()` (SetDisplayConfig `Validate` flag), topology get/apply (`GetCurrentTopology`, `ApplyTopology`), clone-group support for Win10 "virtual mode aware" paths |
| `DisplayConfig.PathTargetInfo` (`PathTargetInfo.cs`) | `DISPLAYCONFIG_PATH_TARGET_INFO` + target mode; rotation/scaling/scanline/frequency; `DesktopImage` → `DISPLAYCONFIG_DESKTOP_IMAGE_INFO` |
| `DisplayConfig.PathTargetSignalInfo` (`PathTargetSignalInfo.cs`) | `DISPLAYCONFIG_VIDEO_SIGNAL_INFO` (pixel rate, h/v sync, active/total size, standard) |
| `DisplayConfig.PathDisplayAdapter` | adapter `LUID` + `DisplayConfigGetDeviceInfo(GetAdapterName)` |
| `DisplayConfig.PathDisplaySource` (`PathDisplaySource.cs`) | source id + GDI name (`GetSourceName`); **DPI scaling via undocumented `DISPLAYCONFIG_DEVICE_INFO_TYPE` values -3 (get) / -4 (set)** — `Native/DisplayConfig/Structures/DisplayConfigGetSourceDPIScale.cs` returns min/current/max *scale steps* relative to the recommended scale, mapped to the `DisplayConfigSourceDPIScale` enum (100–500%) |
| `DisplayConfig.PathDisplayTarget` (`PathDisplayTarget.cs`) | target id + `DISPLAYCONFIG_TARGET_DEVICE_NAME` — `FriendlyName`, `DevicePath`, connector, **EDID manufacture/product ids** (only what CCD exposes — no raw EDID blob parsing; falahati has a separate `EDIDParser` lib); preferred mode (`GetTargetPreferredMode`); boot persistence (`SetTargetPersistence`); virtual-resolution support (types 7/8); `OpenDevicePnPKey()` opens the PnP registry node (where a raw EDID *could* be read) |

`Native/DisplayConfig/DisplayConfigDeviceInfoType.cs` tops out at `SetSupportVirtualResolution = 8` (plus the -3/-4 DPI hack). This enum is the clearest statement of what the library predates.

### 2.2 What's missing for modern needs

- **HDR / Advanced Color — completely absent.** No `GET_ADVANCED_COLOR_INFO (9)`, `SET_ADVANCED_COLOR_STATE (10)`, `GET_SDR_WHITE_LEVEL (11)`, nor the Win 11 22H2+ `SET_HDR_STATE (15)` / `GET_ADVANCED_COLOR_INFO_2 (16)` (which reports actual wire color mode/bit depth). A grep for `hdr|advancedcolor|sdr|whitelevel` returns nothing.
- **SDR white level** (per above) — needed for profile-restoring HDR brightness sliders.
- **DPI scaling** exists but only via the undocumented step-based API; no `GetDpiForMonitor`/per-monitor-v2 integration; no persistence semantics documented.
- **Color depth / format**: `DisplayConfigPixelFormat` is the *source* format (8/16/24/32 bpp) — 10-bit output, chroma format, and dynamic range are invisible (all vendor- or AdvancedColorInfo2-territory).
- **No display-change events** (`WM_DISPLAYCHANGE`, CCD change notifications) — consumers must poll.
- **Refresh-rate quirks**: frequencies are stored as `FrequencyInMillihertz` in target info (good), but DEVMODE-level (`DisplaySetting.Frequency`) is integer Hz, so 59.94 vs 60 mismatches lurk; there's no fractional-rate handling or mode-pruning logic.
- Targets `netstandard2.0`/`net45`; no nullable annotations, no trimming/AOT friendliness, and the last activity is a memory-leak fix in Jan 2023.

Still, the CCD structure marshaling (`Native/DisplayConfig/Structures/*.cs`) and the flag logic in `PathInfo.ApplyPathInfos` (choosing `UseSuppliedDisplayConfig` vs `TopologySupplied`, `SaveToDatabase`, `ForceModeEnumeration`, `NoOptimization`) encode real hard-won knowledge of `SetDisplayConfig` semantics.

---

## 3. NvAPIWrapper

Project `reference/NvAPIWrapper/NvAPIWrapper` (`netstandard2.0;net45`, v0.8.1.101, "for NVAPI 410").

### 3.1 Coverage

High-level namespaces: `Display` (`Display.cs`, `DisplayDevice.cs`, `PathInfo.cs`/`PathTargetInfo.cs` = NvAPI's own display topology via `NvAPI_DISP_Get/SetDisplayConfig`, `CustomResolution.cs` with trial/apply/revert, `Display.OverrideRefreshRate()`, `HDRColorData.cs` + `HDRCapabilities` (V1/V2 structs, HDR10 mastering data), `ColorData` (color format/depth/range control), DVC digital vibrance, HUE, scan-out warping/intensity); `GPU` (`PhysicalGPU.cs` + clocks/coolers/thermal/power/perf-states/illumination/ECC); `Mosaic` (`GridTopology.cs`, `GridTopologyDisplay.cs`, `Topology*.cs` — both Mosaic "phase 1" grid API and "phase 2" topology API, `SetGridTopologies`, `ValidateGridTopologies`, overlap/bezel via `Overlap.cs`); `DRS` (`DriverSettingsSession/Profile/ProfileSetting` + `KnownSettingId.cs` — full driver-settings/app-profile management); `Stereo`.

**Not wrapped**: GSync sync-board module (function IDs like `NvAPI_GSync_GetTopology = 0x4562BC38` sit in `Native/Helpers/FunctionId.cs` but have no delegates), D3D, OpenGL, Video (per README "What's Supported"). G-Sync *monitor* control isn't a thing in NVAPI anyway (that's monitor/driver-settings DRS territory).

### 3.2 P/Invoke technique (the interesting part)

NVAPI exports exactly one useful symbol; everything else is resolved at runtime:

1. `Native/Helpers/DelegateFactory.cs` — `DllImport("nvapi64", EntryPoint="nvapi_QueryInterface")` (and `nvapi` for x86). `GetDelegate<T>()` reads the `[FunctionId]` attribute off a delegate type (`Native/Delegates/Display.cs`, `Mosaic.cs`, …), calls `nvapi_QueryInterface(id)`, wraps the pointer with `Marshal.GetDelegateForFunctionPointer`, and caches it. The 32-bit hash ids live in `Native/Helpers/FunctionId.cs` (extracted by `FunctionIdExtractor.ps1` from NVIDIA headers).
2. **Versioned structs**: every NVAPI struct starts with a version dword. `Native/General/Structures/StructureVersion.cs` packs it exactly like NVIDIA's `MAKE_NVAPI_VERSION`: `_version = Marshal.SizeOf(type) | (version << 16)`. Structs are declared per-version (`HDRColorDataV1`/`V2`, `PathInfoV1`/`V2`, `GridTopologyV1`/`V2` in `Native/*/Structures/`) tagged with `[StructureVersion(n)]`.
3. **Version negotiation**: delegate parameters carry `[Accepts(typeof(HDRColorDataV2), typeof(HDRColorDataV1))]`; `Native/Helpers/ExtensionMethods.Instantiate<T>()` uses reflection to prefill the version field and any fixed-size arrays/strings (via `IInitializable`/`IAllocatable`), and callers fall back from newest to oldest struct on `IncompatibleStructureVersion`.
4. `ValueTypeArray`/`ValueTypeReference` (`Native/Helpers/Structures/`) marshal boxed structs to unmanaged memory for the many array-taking entry points.

This layered pattern (hash-id → delegate → versioned struct → high-level class) is the canonical way to bind NVAPI from .NET and is worth reusing conceptually — but the reflection-heavy `Instantiate<T>` is slow and **hostile to trimming/NativeAOT**; a modern rewrite would use source-generated `[LibraryImport]`-style marshaling or `unsafe` fixed structs.

### 3.3 Maintenance state and limitations

- Frozen at NVAPI R410 (2018): predates DLSS-era APIs, newer HDR/colorimetry entry points, and any post-2020 GPU features. Last commit Dec 2020.
- `DelegateFactory.NvAPI_QueryInterface` **throws for any 32-bit process on a 64-bit OS** ("32bit process running in a 64bit environment can't access NVIDIA API") — factually wrong (WoW64 processes can load the 32-bit nvapi.dll), a deliberate but incorrect guard.
- No ARM64 story; AnyCPU with runtime dll-name switching only.
- DisplayMagician again is the tell: it vendored and rewrote the NVIDIA layer (`DisplayMagicianShared/NVIDIA`) rather than keep depending on this package.

---

## 4. Synthesis for Vantage

### What the Helios lineage got right

- **Capture-then-diff profiles**: a profile is just "what `QueryDisplayConfig` said, serialized"; "active profile" is computed by re-capturing and comparing (`Profile.IsActive`). No fragile state tracking. Keep this.
- **Target identity by PnP device path** (trimmed of instance GUIDs, `PathTarget.DevicePath`) rather than by source number — survives Windows renumbering `\\.\DISPLAY1..n`. Keep, but augment with EDID mfr+product+serial for monitor moves between ports.
- **Vendor topology as an attachment**: `SurroundTopology` hangs off a `PathTarget` instead of forking the whole profile schema. The right extensibility point for per-vendor blobs (NVIDIA Mosaic, AMD Eyefinity, HDR state, DPI, wallpaper…).
- **Order-of-operations knowledge**: apply Mosaic first, then CCD paths; tear down Surround by re-applying 1×1 grids; `SetDisplayConfig` flag selection in `WindowsDisplayAPI.PathInfo.ApplyPathInfos`. Encode this as an explicit state machine, not sleeps.
- Shortcut + run-game + auto-rollback UX, and per-profile rendered icons (`ProfileIcon.cs`) — genuinely good product ideas.

### What it got wrong

- Timing by `Thread.Sleep(2000/18000/10000)` instead of listening for display-change events and re-validating; UI (`MessageBox`) inside domain code; global swallowed exceptions; WCF/WinForms/.NET 4.5 platform lock-in; `TypeNameHandling.Auto`; steamdb scraping with TLS validation off; NVIDIA-only vendor support; heuristic Surround detection cross-matched by position+resolution.

### Carry forward vs. build fresh

**Worth carrying (as knowledge/reference, mostly not as dependencies):**
- The **profile schema shape** (Profile → Path → Target → vendor extension) from Helios `Shared/Topology`.
- WindowsDisplayAPI's CCD struct definitions and `SetDisplayConfig` flag logic; the **undocumented DPI-scale device-info types (-3/-4)** in `DisplayConfigGetSourceDPIScale.cs` (relative-step model included).
- NvAPIWrapper's **QueryInterface + versioned-struct binding pattern**, its `FunctionId` hash table, and the Mosaic `GridTopology` model — reimplemented with `[LibraryImport]`/source-generated marshaling for .NET 8+/AOT.

**Build fresh on raw CCD + modern Windows APIs instead of adopting the libraries:**
- Both libraries are LGPL, netstandard2.0-era, reflection-heavy, unmaintained, and missing everything Vantage exists for: **HDR toggle (`DISPLAYCONFIG_SET_HDR_STATE`, Win11 22H2+), Advanced Color Info 2 (real wire bit-depth/format), SDR white level, per-monitor DPI, fractional refresh rates, display-change eventing** (`WM_DISPLAYCHANGE` + `DISPLAYCONFIG` polling or `IDisplayConfig`-style WinRT `DisplayInformation.AdvancedColorInfo`).
- DisplayMagician already ran this experiment: it forked Helios and ended up rewriting all native layers in-repo (`DisplayMagicianShared/{NVIDIA,AMD,Intel,Windows}`). Vantage should start where that conclusion ends — a small, owned, source-generated interop layer over CCD + vendor SDKs, with Helios's profile model as the conceptual blueprint and none of its runtime machinery.
