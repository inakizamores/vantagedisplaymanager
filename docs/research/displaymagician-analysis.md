# DisplayMagician — Deep Code Analysis

**Purpose:** Research input for "Vantage: Display Manager" (next-gen Windows 11 display-profile manager).
**Source analyzed:** `reference/DisplayMagician` (clone of github.com/terrymacdonald/DisplayMagician, current `main` as of 2026-08, post-v2.7-era codebase on .NET 10).
**All paths below are relative to** `C:\Users\inaki\Documents\GitHub\vantagedisplaymanager\reference\DisplayMagician\`.

---

## 1. Solution structure

`DisplayMagician.sln` contains 6 projects:

| Project | Type / TFM | Purpose |
|---|---|---|
| `DisplayMagician\DisplayMagician.csproj` | WinExe, `net10.0-windows10.0.19041.0`, x64, WinForms (+`UseWPF=true` for a few WPF types) | Main GUI app: forms, tray, shortcuts, game libraries, hotkeys, CLI verbs |
| `DisplayMagicianShared\DisplayMagicianShared.csproj` | Library, `net10.0-windows10.0.19041.0` | **The display engine.** All Windows CCD/GDI + NVIDIA/AMD/Intel capture & apply logic, profile model, profile repository |
| `DisplayMagicianConsole\DisplayMagicianConsole.csproj` | Console exe, `net10.0-windows10.0.19041.0` | Headless CLI (`ChangeProfile`, `CurrentProfile`, `AllProfiles`, `CreateProfile`) |
| `DisplayMagicianIdentityPkg` | MSIX sparse-package project | Gives the unpackaged Win32 app **package identity** (needed for reliable toast notifications; `AppxManifest.xml`, registered from `Program.EnsurePackageIdentity()`) |
| `DisplayMagicianPackage` | WiX 7 (`WixToolset.Sdk 7.0.0` via `global.json`) | Per-machine MSI (`Package.wxs`, `DisplayMagicianComponents.wxs`, context-menu cleanup actions) |
| `DisplayMagicianBundle` | WiX bundle | Bootstrapper EXE that chains the .NET runtime (`NetCorePackage.wxs`) + MSI + identity package (`InstallIdentityPkg.cmd`) |

**Versioning:** Nerdbank.GitVersioning (`version.json`, v3.0.0 line). Build scripts: `build_displaymagician.ps1`, `prepare_displaymagician.ps1`, self-signed cert script.

**Key NuGet packages** (main app): `Newtonsoft.Json 13.0.4`, `NLog 6.1.3`, `McMaster.Extensions.CommandLineUtils 5.1` (CLI), `Microsoft.Toolkit.Uwp.Notifications 7.1.3` (toasts), `AutoUpdater.NET 1.9.2`, `Vortice.DirectInput 3.8.3` (hotkeys!), `ValveKeyValue` + `protobuf-net` (Steam/Uplay parsing), `YamlDotNet`, `System.Management`, `WinCopies.IconExtractor`, `MintPlayer.IconUtils`. Shared lib adds `EDIDParser 1.2.5.4`.

**Vendored binary DLLs (no source in repo)** in `DisplayMagicianShared\DLL\` and `DisplayMagician\DLL\`:
`NVAPIWrapper.dll` + `NVIDIAExportsDll.dll`, `ADLXWrapper.dll` + `AMDExportsDll.dll`, `IGCLWrapper.dll`, `WindowsWallpaperWrapper.dll`, `AudioSwitcher.AudioApi(.CoreAudio).dll`, `IconLib.dll`, `ImageListViewCore.dll`. These are the author's own wrapper builds (DTO-style C# bindings around NVAPI/ADLX/IGCL) — a significant supply-chain/reproducibility weakness.

**COM references:** `IWshRuntimeLibrary` (create .lnk shortcuts), `NETWORKLIST`, `Shell32`.

---

## 2. Core display engine (`DisplayMagicianShared`)

### 2.1 Architecture

Four parallel "library" singletons, each capturing and applying its own config struct:

| Class | File | Wraps | Config struct |
|---|---|---|---|
| `WinLibrary` | `Windows\WinLibrary.cs` (3,307 lines) | Windows CCD + GDI | `WINDOWS_DISPLAY_CONFIG` |
| `NVIDIALibrary` | `NVIDIA\NVIDIALibrary.cs` (4,029 lines) | NVAPI via `NVAPIWrapper.dll` | `NVIDIA_DISPLAY_CONFIG` |
| `AMDLibrary` | `AMD\AMDLibrary.cs` (4,591 lines) | ADLX via `ADLXWrapper.dll` **plus** legacy ADL2 P/Invoke (`AMD\ADL.cs`, 3,110 lines) | `AMD_DISPLAY_CONFIG` |
| `IntelLibrary` | `Intel\IntelLibrary.cs` (2,739 lines) | Intel IGCL via `IGCLWrapper.dll` | `INTEL_DISPLAY_CONFIG` |

All four follow the same pattern: eager static singleton (`private static X _instance = new X()`), `GetLibrary()`, `IsInstalled`, `GetActiveConfig()` / `UpdateActiveConfig()`, `SetActiveConfig()`, `SetActiveConfigOverride()`, `IsActiveConfig()` / `IsValidConfig()` / `IsPossibleConfig()`, `GetCurrentDisplayIdentifiers()` / `GetAllConnectedDisplayIdentifiers()`.

`ProfileItem` (`ProfileItem.cs`, 2,455 lines) holds one instance of **each** of the four config structs, and `ProfileRepository` (`ProfileRepository.cs`, 1,636 lines) is a static repository handling load/save/apply.

Vendor library presence is gated by PCI vendor ID scan — `WinLibrary.IsPCIVideoCardVendorInstalled()` / `GetAllPCIVideoCardVendors()` (WinLibrary.cs:2859); NVIDIA = `"10DE"`.

### 2.2 Capture (`WinLibrary.GetWindowsDisplayConfig`, WinLibrary.cs:774)

P/Invoke surface lives in `Windows\CCD.cs` (1,259 lines): `GetDisplayConfigBufferSizes`, `QueryDisplayConfig`, `SetDisplayConfig`, `DisplayConfigGetDeviceInfo` (11 overloads for source name, target name, preferred mode, adapter name, target persistence, advanced color info, SDR white level, DPI scale), `DisplayConfigSetDeviceInfo` (target persistence, advanced color state, DPI scale), `SetProcessDpiAwarenessContext`. `Windows\GDI.cs` (839 lines) wraps `EnumDisplayDevices`, `EnumDisplaySettings`, `ChangeDisplaySettingsEx`.

A captured `WINDOWS_DISPLAY_CONFIG` (WinLibrary.cs:131) contains:
- `DISPLAYCONFIG_PATH_INFO[] DisplayConfigPaths` and `DISPLAYCONFIG_MODE_INFO[] DisplayConfigModes` — the **raw** CCD arrays from `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` (with the documented double-query retry for `ERROR_INSUFFICIENT_BUFFER`).
- `List<ADVANCED_HDR_INFO_PER_PATH> DisplayHDRStates` — per-target `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO` + `DISPLAYCONFIG_SDR_WHITE_LEVEL` (skipped for analogue connectors via an output-technology block list, WinLibrary.cs:901).
- `Dictionary<string, GDI_DISPLAY_SETTING> GdiDisplaySettings` — per-device `DEVMODE` via GDI (`GetGdiDisplaySettings`, WinLibrary.cs:1188). Kept mainly to detect refresh-rate changes CCD misses.
- `Dictionary<string, List<DISPLAY_SOURCE>> DisplaySources` — GDI view name → sources (adapter LUID, sourceId, targetId, monitor device path, **per-source DPI scaling**).
- DPI scaling captured via the **undocumented** `DISPLAYCONFIG_SOURCE_DPI_SCALE_GET` (device-info type -3/-4), `GetDPISettings` (WinLibrary.cs:620) / `SetDPISettings` (WinLibrary.cs:671). Process DPI-awareness context is *mutated globally* around these calls (`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` ↔ `UNAWARE_GDISCALED`).
- `Dictionary<ulong,string> DisplayAdapters` — adapter LUID → adapter device path, used to re-map LUIDs later.
- `Dictionary<Rect, TaskbarPosition> TaskbarPositions` — from `Windows\TaskbarHelper.cs` (Shell_TrayWnd scraping).
- `IsCloned` + clone patching: cloned paths get `ModeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID` and virtual target IDs are re-mapped onto real unused physical target IDs (parsed as `UID(\d+)#` out of device paths, WinLibrary.cs:834-843, 1085-1147).
- `List<string> DisplayIdentifiers` — see 2.4.

### 2.3 Apply (`WinLibrary.SetActiveConfig`, WinLibrary.cs:2078)

1. If profile is cloned, try `TryBuildCloneTopologyFromCurrentTargets` to rebuild path/mode arrays from *current* runtime targets (saved clone arrays go stale).
2. `SetDisplayConfig(..., DISPLAYMAGICIAN_VALIDATE)` where `DISPLAYMAGICIAN_VALIDATE = SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE` (CCD.cs:217-218). On `ERROR_INVALID_PARAMETER` + cloned, falls back to `TryApplySpecificCloneTopology`, then `TryApplyCloneTopologyFromDatabase` (`SDC_TOPOLOGY_CLONE` from the Windows database).
3. Apply attempt #1 with `DISPLAYMAGICIAN_SET = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`, then `Thread.Sleep(delayInMs)`.
4. Attempt #2: identical call after `Thread.Sleep(delayInMs*2)` — "sometimes it doesn't work the first time".
5. Attempt #3: `SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_CHANGES` (topology only, let Windows pick modes).
6. Post-apply: set per-source DPI via `DISPLAYCONFIG_SOURCE_DPI_SCALE_SET`; re-assert HDR state per target via `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE` (only toggles on/off — API can't set more).
7. Helpers: `WakeUpAllDisplays` (WinLibrary.cs:1643), `EnableAllConnectedDisplays` (:1686), `ForceRestartExplorer` (:1670 — restarts explorer.exe to fix lost taskbars, exposed per-profile as `ProfileItem.ForceExplorerRestart`).

Cross-vendor orchestration lives in `ProfileItem.SetActive` (ProfileItem.cs:783): wake displays → NVIDIA `SetActiveConfig` (Mosaic) → AMD (Eyefinity) → Intel (Combined Display) → `WinLibrary.UpdateActiveConfig()` → `WinLibrary.SetActiveConfig` (CCD layout/DPI/HDR) → vendor `SetActiveConfigOverride` (NVIDIA per-display HDR/color/DRS; AMD color/gamma; Intel overrides). Before any Surround/Eyefinity/Combined apply it calls `WinLibrary.EnableAllConnectedDisplays()` because vendor APIs need all source monitors active. `Thread.Sleep(delayInMs)` between every step (default 500 ms, so a full apply is seconds of sleeping).

### 2.4 Display identity & profile equality

- Windows identifier format (`GetSomeDisplayIdentifiers`, WinLibrary.cs:2661): `"WINAPI|<adapterDevicePath>|<outputTechnology>|<targetId>|<monitorDevicePath>|<monitorFriendlyName>"` joined with `|`. NVIDIA/AMD/Intel produce their own analogous identifier strings; `ProfileRepository.GetCurrentDisplayIdentifiers()` (ProfileRepository.cs:1385) merges them, dropping Windows-side entries already covered by a vendor and **filtering surround virtual displays by substring match** (`"NV Surround"`, `"AMD"`, `"Eyefinity"` — ProfileRepository.cs:1454-1465).
- **Profile equality is exact structural equality** — `ProfileItem.Equals` (ProfileItem.cs:2393) = NVIDIA config equal AND AMD equal AND Intel equal AND Windows equal AND identifier lists `SequenceEqual`. Every nested struct hand-implements `IEquatable` with logging inside `Equals` and numerous commented-out fields that "changed after reboot" (AdapterId, path/source IDs, DeviceKey, DisplaySources ordering...). Device-path comparison skips instance-ID segments by magic index (`#`-split part 1 in `DISPLAY_SOURCE.Equals` WinLibrary.cs:98-113; part 4 in `WINDOWS_DISPLAY_CONFIG.Equals` :187-206).
- Matching current state to a saved profile = re-capture everything (`ProfileRepository.UpdateActiveProfile`, ProfileRepository.cs:685) and linear-scan `Equals` over all profiles.
- **Adapter LUID drift:** Windows reassigns adapter LUIDs on every reboot, so every load runs `WinLibrary.PatchWindowsDisplayConfig` (WinLibrary.cs:426) which maps old→new LUIDs by adapter device path (`GetAdapterIdMap`, :355) and rewrites the saved paths/modes in memory. NVIDIA has an equivalent `PatchNVIDADisplayConfig` (NVIDIALibrary.cs:3971).

---

## 3. Vendor-specific paths

### NVIDIA (`NVIDIA\NVIDIALibrary.cs`)
- Binding: author-built `NVAPIWrapper.dll` (`NVAPIApiHelper.Initialize()`, DTO types like `NVAPIMosaicCurrentTopoDto`, `NVAPIHdrColorDataDto`). Not the public "NvAPIWrapper.Net" — a private facade. `nvapi64.dll` loaded underneath; `NVIDIAExportsDll.dll` is loaded purely to keep hybrid-GPU laptops' dGPU awake (`KeepVideoCardOn()`, :1166).
- `NVIDIA_DISPLAY_CONFIG` (:861): `MosaicConfig` (current topo + grid topologies), `PhysicalAdapters` dict (per-adapter config), `DRSSettings` (driver-settings base profile snapshot), per-display configs, display identifiers.
- `NVIDIA_PER_DISPLAY_CONFIG` (:112) is enormous: HDR capabilities + color data, adaptive sync, color data, **custom displays** (`NVAPICustomDisplayDto` list), DisplayPort info, virtual refresh rate, stereo, source color space, HDR metadata/tone mapping, infoframes, monitor capabilities/colorimetry, timing, scanout config.
- Surround: `SetActiveConfig` (:2602) applies Mosaic via `mosaicHelper.SetDisplayGrids(...)`; `TurnOffMosaic` (:2674) first tries a 1×1 grid per display, verifies with `GetCurrentTopo()`, falls back to `EnableCurrentTopo(false)`.
- `SetActiveConfigOverride` (:2740) applies DRS settings (diffs stored base profile vs active, sets changed, **restores unset settings to default** to avoid leakage), then per-display HDR color data, custom resolutions etc.
- HDR: yes — captured via NVAPI HDR color data *and* Windows advanced color info; both re-applied.

### AMD (`AMD\AMDLibrary.cs` + `AMD\ADL.cs`)
- Primary binding: `ADLXWrapper.dll` (ADLX — `IADLXEyefinityDesktop` QueryInterface plumbing visible at :2246-2266). Captures per-GPU and per-display settings: FreeSync, VSR, GPU scaling, scaling mode, integer scaling, color depth, pixel format, custom color (brightness/contrast/saturation/temperature), custom resolutions (TODO comment at :2422 — ADLX wrapper "does not support custom resolutions properly yet"), 3D-LUT/gamut/gamma structs, Vari-Bright.
- **Legacy ADL2 still used for Eyefinity**: constructor loads `atiadlxx.dll` via `ADLImport.ADL2_Main_Control_Create` (:1829-1858) because ADL "seems more reliable" for SLS; sets `ADL_4KWORKAROUND_CANCEL` env var (:1864). `SetActiveConfig(config, useADLEyefinity, delayInMs)` (:3426) can drive Eyefinity through either ADLX or ADL SLS mapping (`AMD_SLSMAP_CONFIG`, `ADL_SLS_MAP/TARGET/MODE/OFFSET`, bezel/transient modes).
- `AMD_DISPLAY_CONFIG` (:1675): `IsInUse`, `IsEyefinity`, GPUs with settings, displays with settings, SLS config, display identifiers.

### Intel (`Intel\IntelLibrary.cs`)
- Binding: `IGCLWrapper.dll` (Intel Graphics Control Library). `INTEL_DISPLAY_CONFIG` (:557): adapters, displays-with-settings, Combined Display state (`CombinedDisplayIsInUse`), identifiers. Same singleton/capture/apply/override shape. Least-developed of the three.

---

## 4. Profile storage format

- Location: `%LOCALAPPDATA%\DisplayMagician\Profiles\DisplayProfiles.json` (`ProfileRepository.cs:70-76`). Written **UTF-16** (`Encoding.Unicode`), indented.
- Envelope: `ProfileFile { string ProfileFileVersion /* "4" */, DateTime LastUpdated, List<ProfileItem> Profiles }` (ProfileRepository.cs:36).
- `ProfileItem` serialized members: `UUID` (GUID string), `Name`, `[JsonRequired] NVIDIADisplayConfig`, `[JsonRequired] AMDDisplayConfig`, `[JsonRequired] IntelDisplayConfig`, `[JsonRequired] WindowsDisplayConfig`, `ProfileDisplayIdentifiers` (List<string>), `WallpaperConfiguration`, `SavedProfileIconCacheFilename`, `ApplyProfileCount` (Samsung Odyssey G9 needs 2 applies — ProfileItem.cs:447), `ApplyProfileDelay`, `ForceExplorerRestart`, plus `ProfileBitmap`/`ProfileTightestBitmap` — **PNG bitmaps base64-embedded in the JSON** via `CustomBitmapConverter` (ProfileItem.cs:108-160).
- Serializer settings (ProfileRepository.cs:807-819 load / 1079-1092 save): Newtonsoft, `TypeNameHandling.Auto` (polymorphic `$type` — used by wallpaper config; a deserialization-gadget risk), `MissingMemberHandling.Ignore` on load / `.Error` on save, `DefaultValueHandling.Populate`, error-collector delegate.
- Migration/versioning: crude. `Utils.OldFileVersionsExist/UpgradeOldFileVersions` renames legacy `DisplayProfiles_*.json` (e.g. `DisplayProfiles_2.2.json`) to the new name; a real JSON-migration function `MigrateJsonToLatestVersion` exists but is **commented out** (ProfileRepository.cs:953-1035). Fallback path deserializes bare `List<ProfileItem>` if the envelope fails. Breaking format changes historically required users to recreate profiles.
- Reliability hack: `SaveProfiles` re-reads the file and structurally compares (`ValidateProfiles`) — if mismatch, sleeps 1 s and writes again (ProfileRepository.cs:1120-1146).
- Icons cached separately as `Profiles\profile-<UUID>.ico` (`SaveProfileIconToCache`, :1305). Wallpapers stored per-profile under `%LOCALAPPDATA%\DisplayMagician\Wallpaper\` keyed by UUID + monitor-bounds FNV-1a hash (`Wallpaper.cs`).
- Shortcuts: `Shortcuts\Shortcuts.json`, envelope version "5" (`ShortcutRepository.cs:68-70`). Settings: `Settings.json`, version "5" (`ProgramSettings.cs:47`).

---

## 5. Shortcuts & game integration

- Model: `ShortcutItem.cs` (2,038 lines). Categories: `Executable`, `Game`, `NoGame`, `Application` (UWP) (ShortcutItem.cs:32). Each shortcut bundles: profile UUID to apply, audio/capture device + volumes, start/stop/after program lists (`StartProgram`/`StopProgram`/`AfterProgram` structs with priority ordering, admin flag, close-on-finish), game or exe to run, process to monitor, and **three independent permanence flags** — `DisplayPermanence`, `AudioPermanence`, `CapturePermanence` (`Temporary` = revert on exit; ShortcutItem.cs:205-207).
- Launcher detection (`DisplayMagician\GameLibraries\`): each library is a `GameLibrary` subclass (GameLibrary.cs:21) enumerated at startup:
  - **Steam** (`SteamLibrary.cs`): install path from `HKLM\SOFTWARE\...\Steam`, installed appIDs from `HKCU\...\Steam\Apps` (`Installed=1`), metadata from binary `appcache\appinfo.vdf` (custom `AppInfo`/ValveKeyValue reader), extra library folders from `libraryfolders.vdf`/`config.vdf` **parsed with regexes** (SteamLibrary.cs:588, 610).
  - **Origin/EA** (`OriginLibrary.cs`): `HKLM\SOFTWARE\WOW6432Node\Origin` + local content `.mfst` manifests (876 lines, messiest).
  - **Uplay/Ubisoft Connect** (`UplayLibrary.cs`): `HKLM\...\Ubisoft\Launcher\Installs` + protobuf `configurations` file (`UplayFileStructure.cs`, protobuf-net).
  - **Epic** (`EpicLibrary.cs`): EOS registry + `ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON.
  - **GOG** (`GOGLibrary.cs`): `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\*`.
  - **Xbox/UWP** (`XboxLibrary.cs`): `Windows.Management.Deployment.PackageManager` enumeration.
  - **No Battle.net support.**
- Launch: protocol URIs where possible — `uplay://launch/{id}` (UplayGame.cs:215), `com.epicgames.launcher://apps/{id}?action=launch&silent=true` (EpicLibrary.cs:572), `steam://rungameid` equivalent in `SteamGame.cs`; otherwise direct exe via `Processes\ProcessUtils.StartProcess` (handles priority + run-as-admin).
- Run flow: `ShortcutRepository.RunShortcut(shortcut, cancelToken)` (ShortcutRepository.cs:866, ~1,750 lines, one method):
  1. Snapshot rollback state: `rollbackProfile = ProfileRepository.CurrentProfile` (:901), current default playback/comms/capture devices + volumes (:951-1124).
  2. Apply target profile via `ProfileRepository.ApplyProfile` (:931).
  3. Switch audio via **AudioSwitcher CoreAudioController** (see §7).
  4. Run stop-programs (kill processes, optionally restart later) and start-programs interleaved by priority (:1216-1352), `WinLibrary.RefreshTrayArea()` after kills.
  5. Start game/app; wait for launcher; then **poll every 1000 ms** (`Thread.Sleep(1000)` loops, e.g. :1429-1444, :1515-1533) for the monitored process (game exe, alternative exe, or launcher processes) to exit, with toast notifications and a cancel button wired through `ToastNotificationManagerCompat`.
  6. On exit: kill `CloseOnFinish` started programs, restart stopped programs, revert audio/capture (only if `Permanence == Temporary`, :2437-2517), revert display profile (:2526-2547), run after-programs (:2555), reset tray text.
- Runs on a background thread started from `Program.RunShortcutTask` (semaphore-gated, one at a time; `Program.WaitingForGameToExit` flag).

---

## 6. App plumbing

- **UI:** WinForms (`DisplayMagician\UIForms\` — `MainForm`, `DisplayProfileForm`, `ShortcutForm` (3,971 lines!), `ShortcutLibraryForm`, `SettingsForm`, `HotkeyForm`, FOV calculator, etc.), custom-drawn `ImageListView` renderers for profile/shortcut galleries. `DisplayMagicianShared\UserControls\DisplayView.cs` draws the monitor-layout picture from `ProfileItem.Screens` (`ScreenPosition` list).
- **Tray:** `NotifyIcon` on `MainForm` (MainForm.cs:79-81) with context-menu of profiles/shortcuts (`RefreshNotifyIconMenus`, :399); double-click action configurable (`NotifyIconDoubleClickAction`, ProgramSettings.cs:35). Minimise-to-tray with optional toast.
- **Hotkeys:** *not* `RegisterHotKey` — global capture via **DirectInput polling** (`DirectInputManager.cs`, 1,113 lines, Vortice.DirectInput): keyboard combos (`HotkeyKeyboard`) *and joystick/gamepad buttons* (`HotkeyJoystick`); tasks = change profile / run shortcut / open windows / exit (`HotkeyTask`, DirectInputManager.cs:21). Stored in `Settings.json`.
- **CLI:** the GUI exe itself parses verbs with McMaster (`Program.cs:527-`): `RunShortcut <uuid>`, `ChangeProfile <uuid>`, `CreateProfile`, `--debug/--trace`. Separate `DisplayMagicianConsole` exe adds scripting-friendly `CurrentProfile`/`AllProfiles`/`CreateProfile -force` with `-v`/`-p` (parseable) flags and rich ERRORLEVELs (Console `Program.cs:19-32`) — it links `DisplayMagicianShared` directly, so it applies profiles in-process without the GUI.
- **Single instance:** `SingleInstance.cs` — global `Mutex` + `NamedPipeServerStream` (`Pipe_DisplayMagician`); second instance forwards its argv over the pipe and exits; first instance queues commands until `MarkReadyForCommands()` (Program.cs:523) and dispatches `RunShortcut`/`ChangeProfile`/`CreateProfile` or foregrounds the window (SingleInstance.cs:48-120).
- **Elevation:** `app.manifest` = `asInvoker` — **no admin required** for display switching. `RunAsAdministrator` flags exist per started program; AutoUpdater runs its installer elevated (`AutoUpdater.RunUpdateAsAdmin = true`, Program.cs:1702).
- **Package identity:** `EnsurePackageIdentity()` registers the sparse MSIX (`DisplayMagicianIdentityPkg.msix`, AUMID `LittleBitBig.DisplayMagician`) so toasts survive; also handles toast activation re-entry (`AppToastActivated`).
- **Auto-start:** `StartupManager.cs` writes the HKCU Run key; reconciled on every launch against `ProgramSettings.StartOnBootUp` (Program.cs:348-366).
- **Upgrade:** AutoUpdater.NET against a hosted appcast JSON (custom `ParseUpdateInfoEvent`), remind-later timer, downloads and runs installer elevated. `ConfigMigrationRunner.cs` runs rule-based settings migrations (currently one rule: `SettingsV4ToV5DonationSplitMigration`) with status enum + user-facing recovery prompt (`RecoverProgramSettingsFile`).
- **Desktop context menu:** `ContextMenu.cs` installs registry-based right-click desktop menu entries for profiles/shortcuts (re-written every startup).
- **Wallpaper:** `DisplayMagicianShared\Wallpaper.cs` + `WindowsWallpaperWrapper.dll` (IDesktopWallpaper COM underneath) — per-profile capture/apply of per-monitor wallpapers, slideshow, solid color; polymorphic `WindowsWallpaperConfig` serialized with `TypeNameHandling`.
- **Logging:** NLog to `%LOCALAPPDATA%\DisplayMagician\Logs\DisplayMagician-<timestamp>.log`, level from settings or `--debug/--trace`, 40 MB archive threshold (Program.cs:185-204).

---

## 7. Audio switching

Yes. `ShortcutRepository` owns a lazily-created **`AudioSwitcher.AudioApi.CoreAudio.CoreAudioController`** (ShortcutRepository.cs:72-85; the DLLs are vendored, not NuGet — presumably rebuilt for modern .NET). Per shortcut it can set: default playback device, default *communications* playback device, playback volume, default capture device, default comms capture device, capture volume (`SetAsDefault()` / `SetAsDefaultCommunications()` / `SetVolumeAsync(...).Wait(2000)`). Original devices/volumes are snapshotted before the game and restored afterwards when the respective `*Permanence == Temporary`. Failure to init CoreAudio is non-fatal (logged warn: "Audio Chipset on your computer is not supported"). Audio is **per-shortcut only** — plain display profiles do not switch audio (a frequent user request).

---

## 8. Pain points & code smells (specific)

1. **God classes / god methods.** `AMDLibrary.cs` 4,591 lines; `NVIDIALibrary.cs` 4,029; `WinLibrary.cs` 3,307; `ShortcutForm.cs` 3,971; `ShortcutRepository.RunShortcut` is a single ~1,750-line method mixing profile, audio, process, toast and rollback logic; `Program.cs` 2,189 lines mixes bootstrap, CLI, updater, migrations and task orchestration.
2. **UI thread blocking.** `DisplayProfileForm.Apply_Click` (DisplayProfileForm.cs:56-103) calls `Program.ApplyProfileTask(...)` **synchronously on the UI thread**, then `Thread.Sleep(500)`. The apply pipeline itself sleeps constantly (`ProfileItem.SetActive` sleeps between every vendor step; `WinLibrary.SetActiveConfig` sleeps `delayInMs`, `2×`, `3×` between retries; `NVIDIALibrary` sleeps `delayInMs*3` after Mosaic ops). A profile switch freezes the form for many seconds. Concurrency control is `SemaphoreSlim.Wait(0)` + a non-volatile static `_userChangingProfiles` bool (ProfileRepository.cs:67) — racy by construction.
3. **Brittle equality as the core abstraction.** Profile identity = deep `Equals` over the raw structs of four APIs. Dozens of hand-written `Equals` with fields commented out one by one as Windows/NVIDIA "changed them after reboot" (`ADVANCED_HDR_INFO_PER_PATH.Equals` WinLibrary.cs:41-56; `DISPLAY_SOURCE.Equals` :87-119; `WINDOWS_DISPLAY_CONFIG.Equals` :160-226). Magic-index `#`-split skipping of device-path instance IDs. `WINDOWS_DISPLAY_CONFIG.Equals` compares `DisplaySources` by positional `ElementAt(i)` without checking the other dict's count (:217-224 — can throw or silently pass). Surround filtering by `displayId.Contains("AMD")` (ProfileRepository.cs:1461) will misclassify any monitor whose friendly name contains "AMD". Any new captured field invalidates all previously saved profiles' "active" detection — the root cause of recurring "my profile shows as not matching" bug reports.
4. **GetHashCode broken relative to Equals** everywhere: tuple-hashes over arrays/lists/dicts hash by reference (`WINDOWS_DISPLAY_CONFIG.GetHashCode` WinLibrary.cs:228-233, `ProfileItem.GetHashCode` ProfileItem.cs:2424), violating the hash/equality contract they even cite in comments.
5. **Logging in equality operators.** `Equals` methods emit `SharedLogger.logger.Trace/Debug` on every mismatch — side-effectful comparisons, massive log spam (every profile scan logs), and interpolated strings are built even when the level is off. Trace-level logging on virtually every line of the 4k-line libraries.
6. **Eagerly-initialized interdependent singletons.** `private static WinLibrary _instance = new WinLibrary()` performs live CCD queries in the static initializer (WinLibrary.cs:245); NVIDIA/AMD/Intel constructors probe PCI, load native DLLs and capture full configs at class-load time. Order-sensitive, untestable, and `TypeInitializationException` is caught as an expected control path (NVIDIALibrary.cs:1032).
7. **Copy-paste error handling.** Three nearly identical `RemoveProfile` overloads each duplicating a 5-branch catch chain around `File.Delete` (ProfileRepository.cs:277-492); identical duplicated JSON-load blocks; every catch just logs and returns bool. `LoadProfiles` shows `MessageBox.Show` from library code (ProfileRepository.cs:850) — Shared lib has WinForms dependency baked in.
8. **Serialization risks.** Newtonsoft `TypeNameHandling.Auto` on user-writable files (gadget attack surface); bitmaps embedded as base64 inside profile JSON (bloats file, slows load); UTF-16 encoding; `MissingMemberHandling.Error` on save-validation round-trip makes saves fail on model drift; migrations mostly commented out — versioning is "rename old file and hope".
9. **Sleep/retry engineering.** Fixed `Thread.Sleep` values everywhere instead of event-driven waits (`WM_DISPLAYCHANGE`/`RegisterNotification`): 3× SetDisplayConfig attempts, `SaveProfiles` write-verify-sleep(1000)-rewrite, `ApplyProfileCount`/`ApplyProfileDelay` per-profile knobs to paper over monitor quirks (Odyssey G9), 1 Hz process-exit polling, 10 s fixed wait for "different executable" to appear (ShortcutRepository.cs:1492).
10. **Global process state mutation.** `SetProcessDpiAwarenessContext` toggled back and forth at runtime around DPI calls (WinLibrary.cs:264, 850, 1074, 2302-2318) — affects the whole process and any concurrently-rendering UI.
11. **Vendored closed binaries.** The critical NVAPI/ADLX/IGCL/wallpaper/audio wrappers are checked-in DLLs with no source, pinned to the author's machines; AMD keeps a second full legacy ADL P/Invoke layer alive just for Eyefinity reliability.
12. **No tests.** Zero test projects in the solution; correctness rests on field reports from users with exotic monitor setups.

---

## 9. What to reuse conceptually vs. redesign for Vantage

### Worth reusing (hard-won domain knowledge)
- **Capture-and-replay of raw CCD path/mode arrays** as the backbone of a profile, with `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`, validate-before-apply, and a topology-only fallback. This is proven to work without admin rights.
- **Adapter LUID re-mapping after reboot** (map by adapter device path — `GetAdapterIdMap`/`PatchWindowsDisplayConfig`); any CCD-replay design needs this.
- **Clone handling**: virtual target-ID patching at capture time + rebuilding clone topology from current runtime targets at apply time; falling back to the Windows database clone topology.
- **Stable display identity** built from adapter device path + output technology + target ID + monitor device path with instance-ID segments excluded (do it with parsed EDID vendor/product/serial rather than string surgery).
- **Vendor ordering**: apply Surround/Eyefinity/Combined-Display *before* the Windows CCD layout, force-enable all connected displays first, refresh the Windows view afterwards, then apply per-display vendor overrides (HDR/color/DRS) last. NVIDIA's `TurnOffMosaic` two-strategy teardown (1×1 grids → `EnableCurrentTopo(false)`) is battle-tested.
- **NVIDIA DRS diff-apply with restore-unset-to-default** (NVIDIALibrary.cs:2839-2859) — prevents setting leakage between profiles.
- **DPI scaling capture/apply** via the undocumented `DISPLAYCONFIG_SOURCE_DPI_SCALE_GET/SET` device-info types, and HDR toggle via `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE` (on Win11 24H2+, prefer the newer `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE_2`/SDR-white-level APIs).
- **Feature scope proven valuable**: taskbar position capture, per-monitor wallpaper per profile, explorer-restart escape hatch, per-shortcut temporary vs permanent permanence for display *and* audio *and* capture separately, audio+mic device/volume switching with rollback, launcher URI starts + configurable process-to-monitor, single-instance pipe command forwarding, separate headless console exe with parseable output/errorlevels, desktop context menu + tray menu + hotkeys (incl. joystick) as activation surfaces.

### Redesign deliberately
- **Profile model:** replace "four raw API dumps + exact struct equality" with a normalized, versioned schema (per-display: position, resolution, refresh, rotation, scaling, HDR state, bit depth, primary flag, vendor-extras bag) plus a *matcher* with explicit tolerance rules. Keep the raw CCD blob as an opaque replay payload, but never use it for identity/equality.
- **Equality → scoring:** "is this profile active / possible" should be a semantic comparison over the normalized model (and connected-display set), not deep struct equality; makes profiles resilient to OS/driver field churn.
- **Serialization:** System.Text.Json source-generated, UTF-8, no `TypeNameHandling`, no embedded bitmaps (icon cache on disk), explicit `schemaVersion` with real migration steps.
- **Concurrency:** async apply pipeline (`IProgress<ApplyStep>`, `CancellationToken`), display-change *events* (`WM_DISPLAYCHANGE`, `QueryDisplayConfig` diffing, or `Windows.Devices.Display.Core` watcher) instead of sleeps and 1 Hz polls; never touch the UI thread.
- **Architecture:** DI-injected `IDisplayBackend` implementations (Windows/NVIDIA/AMD/Intel) behind one interface; lazy, probeable initialization; pure capture functions returning immutable models → unit-testable with recorded fixtures. Build/own the NVAPI/ADLX/IGCL interop as source (CsWin32-style or open wrappers), not checked-in DLLs.
- **Hotkeys:** `RegisterHotKey`/Raw Input rather than a DirectInput polling thread (keep gamepad support as an optional listener).
- **Audio:** modern maintained CoreAudio layer (e.g. NAudio.CoreAudioApi or direct `IPolicyConfig`/`IAudioPolicyConfigFactory` interop) instead of abandoned AudioSwitcher binaries.
- **UI:** WinUI 3 / WPF with MVVM; keep WinForms-free shared core (DisplayMagicianShared currently references WinForms and shows MessageBoxes).
- **Logging:** structured (`ILogger`), no logging inside comparisons, level-guarded.
- **Packaging:** MSIX proper (solves identity/toasts natively) or WiX with the sparse-package trick copied; Nerdbank.GitVersioning is fine to keep.
- **Game integration:** consider scoping v1 to profile management + generic "run app with profile & revert" and add launcher catalogs incrementally — DisplayMagician's launcher scrapers (regex-parsing VDFs, Origin manifests) are its highest-maintenance, most breakage-prone area.
