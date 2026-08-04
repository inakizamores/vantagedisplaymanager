# Vantage: Display Manager — Ecosystem & Platform Research

Research date: 2026-08-03. Scope: incumbent pain points, adjacent tools, Windows 11-era platform APIs, UI stack options, and distribution/update patterns for a next-generation Windows 11+ display profile manager.

---

## 1. Known pain points of the incumbents

### 1.1 DisplayMagician — current state

- Repo: https://github.com/terrymacdonald/DisplayMagician — C#/.NET, WinForms UI, actively maintained but by a single developer. The README roadmap admits the UI is dated: a future "v4 using WinUI3" and "Add Unit Tests!" are both still just roadmap items, i.e. the current product has effectively **no unit tests** and a legacy UI.
- It began as a fork of falahati's HeliosDisplayManagement and inherited that architecture; Terry MacDonald later replaced falahati's stale wrapper libraries with his own P/Invoke test beds ([NVIDIAInfo](https://github.com/terrymacdonald/NVIDIAInfo/releases), AMDInfo, CCDInfo) which now live inside `DisplayMagicianShared`.
- v2.7.2 had to add a dedicated "update profile" feature purely because **driver releases kept invalidating saved profiles** ([release notes](https://github.com/terrymacdonald/DisplayMagician/releases/tag/v2.7.2)).

### 1.2 Recurring failure modes (GitHub issues + wiki + forums)

These are the quirks Vantage must engineer around:

1. **Over-strict profile equality → "profile not valid/possible/can't be used".** DisplayMagician decides whether a profile is "current" or "possible" by comparing an enormous serialized settings blob. If *any* value drifts — a driver update, a change in NVIDIA Control Panel/AMD Adrenalin, or Windows itself rewriting a setting — the saved profile no longer matches and becomes unusable. The [troubleshooting wiki](https://github.com/terrymacdonald/DisplayMagician/wiki/Troubleshooting-DisplayMagician) openly documents this ("one or more display settings currently being used being different from the settings in the saved display profile") and the recommended fix is "re-apply and re-save the profile" — i.e., the user pays for the tool's brittleness. Representative issues: ["display profile can't be used as not all displays are connected" #348](https://github.com/terrymacdonald/DisplayMagician/issues/348), [#361](https://github.com/terrymacdonald/DisplayMagician/issues/361), ["Current Display Profile bug" #401](https://github.com/terrymacdonald/DisplayMagician/issues/401).
2. **NVIDIA Surround switching failures.** Crashes when applying Surround, Surround "activating" but not actually switching, and black screens when returning to the base profile that require a reboot: [#131](https://github.com/terrymacdonald/DisplayMagician/issues/131), [#254](https://github.com/terrymacdonald/DisplayMagician/issues/254), [#316](https://github.com/terrymacdonald/DisplayMagician/issues/316), [#398](https://github.com/terrymacdonald/DisplayMagician/issues/398).
3. **Driver updates invalidate profiles.** New driver versions add/remove fields in the NVIDIA/AMD settings structures the profile captured, breaking equality (see 1). The wiki also notes NVIDIA Control Panel caches state and won't show DisplayMagician's changes until restarted.
4. **Profile format breaking changes.** Profiles from before v2.4 are incompatible with v2.4+; Windows 10 profiles don't work after an upgrade to Windows 11 and must be deleted and recreated, including every game shortcut that referenced them (wiki).
5. **Taskbar position corruption.** After switching (especially Surround → extended), the taskbar lands on the wrong monitor or wrong edge: [#52](https://github.com/terrymacdonald/DisplayMagician/issues/52), [#370](https://github.com/terrymacdonald/DisplayMagician/issues/370), [#386](https://github.com/terrymacdonald/DisplayMagician/issues/386); users still request taskbar-position capture as a feature ([#307](https://github.com/terrymacdonald/DisplayMagician/issues/307)). DisplayMagician does this via undocumented `StuckRects3` registry manipulation, which is slow/broken on some machines and has caused hangs ([#351](https://github.com/terrymacdonald/DisplayMagician/issues/351)).
6. **Wrong "main" monitor after switching** — Windows and DisplayMagician disagree about which display is primary ([#386](https://github.com/terrymacdonald/DisplayMagician/issues/386), [#370](https://github.com/terrymacdonald/DisplayMagician/issues/370)).
7. **Slow startup** — the app scans Steam/Origin/Uplay/GOG/Epic game libraries at launch; users on [OverTake](https://www.overtake.gg/threads/displaymagician-automate-display-audio-app-changes-with-a-single-desktop-shortcut.199994/page-3) question why a display manager needs to scan games before its tray icon appears. Startup crashes in `ProfileRepository` also occur ([#7](https://github.com/terrymacdonald/DisplayMagician/issues/7)).
8. **Game-process monitoring is fragile** — it watches the wrong process for some launchers/games and never notices the game exited, so display state is never restored (wiki, e.g. Assetto Corsa Competizione).

### 1.3 HeliosDisplayManagement — abandonment

- https://github.com/falahati/HeliosDisplayManagement — last releases circa 2020/2021; the author stated he "cannot put in the time the project deserves," with development on hold indefinitely ([README](https://github.com/falahati/HeliosDisplayManagement/blob/master/README.md)). Community consensus points users to DisplayMagician instead.
- Important knock-on effect: falahati's ecosystem libraries (`WindowsDisplayAPI`, `NvAPIWrapper`, `EDIDParser`) that both projects depended on are equally stale — anyone building on them inherits ~2019-era NVAPI struct definitions. This is precisely why DisplayMagician rewrote its interop layer.

### 1.4 Design lessons for Vantage

- **Identify monitors by stable identity** (EDID manufacturer + product + serial from `DISPLAYCONFIG_TARGET_DEVICE_NAME` / `DisplayMonitor`), never by adapter LUID or Windows display number, both of which change across boots, docks, and driver updates.
- **Store a minimal, semantically-meaningful profile** (topology + per-target mode + HDR + DPI + refresh) instead of a full driver-settings dump; compare with tolerant, field-by-field equality and treat vendor-private settings as opaque/optional.
- **Validate before applying** (`SDC_VALIDATE`), apply with `SDC_ALLOW_CHANGES`, and implement a timed auto-revert ("keep these settings?") like Windows itself does.
- **Never scan game libraries on the startup path**; tray-first, instant.
- Handle taskbar/window restoration explicitly (or integrate PersistentWindows-style layout restore) since Windows still gets this wrong.
- Version the profile schema with explicit migration from day one.

---

## 2. Competing and adjacent tools — features and gaps

| Tool | What it does well | Persistent complaints / gaps |
|---|---|---|
| **DisplayFusion** ([discussions](https://www.displayfusion.com/Discussions/View/monitor-profiles-no-longer-working/?ID=8ca9c3ad-8d06-4e56-b1ac-1cfc318c47ed)) | Monitor profiles, multi-monitor taskbars, window management, triggers | Paid; profiles [get lost / stop matching](https://www.displayfusion.com/Discussions/View/monitor-configuration-profiles-getting-lost-and-not-working-after-different-setp/?ID=5451ac09-df21-497d-8934-9d351c0819f7) when hardware IDs shift (docks/USB-C); heavyweight; UI predates Win11 Fluent |
| **Dell Display Manager 2 / DDPM** ([Dell KB](https://www.dell.com/support/kbdoc/en-us/000287285/dell-display-and-peripheral-manager-for-windows)) | DDC/CI settings, brightness, input switching, KVM wizard, Easy Arrange | Dell monitors only; requires DDC/CI enabled or almost nothing works; [Network KVM plugin force-installed on update](https://www.dell.com/community/en/conversations/monitors/ddmddpm-network-kvm-feature/65de17f696dcc66415a23c66) annoyed users. Vendor tools (LG OnScreen Control, Samsung Easy Setting Box…) fragment per brand — an opening for one neutral tool |
| **NirSoft MultiMonitorTool** ([blog review](https://www.containsmoderateperil.com/blog/2025/9/19/multimonitortool)) | Free, scriptable save/load of monitor config | No UI polish, no tray workflow; known bug where [only one of two monitors is restored](https://github.com/Nonary/MonitorSwapAutomation/issues/9); no HDR/DPI awareness |
| **Monitor Profile Switcher (martink87)** ([SourceForge](https://sourceforge.net/projects/monitorswitcher/)) | Tiny tray app, XML profiles, hotkeys | Effectively unmaintained (community [forks](https://github.com/Matt-17/Monitor-Profile-Switcher) exist); breaks when Windows re-indexes monitor IDs on USB-C hubs — users must re-save profiles ([site FAQ](https://monitorprofileswitcher.com/)); no HDR/refresh/DPI |
| **ControlMyMonitor (NirSoft)** ([site](https://www.controlmymonitor.com/), [input-switch article](https://www.nirsoft.net/articles/set_monitor_input_source_command_line.html)) | Full VCP code read/write, CLI (`/SetValue "\\.\DISPLAY2\Monitor0" 60 18`) | Raw and expert-only; input values differ per monitor ("trial and error"); no profiles/automation UI |
| **PersistentWindows** ([repo](https://github.com/kangyu-california/PersistentWindows)) | Auto-captures and restores window layout + taskbar per monitor-setup, RDP/sleep resilient | Solves only window layout, not display config; needs admin to manage elevated windows; occasional misses ([#321](https://github.com/kangyu-california/PersistentWindows/issues/321)) |
| **Twinkle Tray** ([repo](https://github.com/xanderfrangos/twinkle-tray)) / **Monitorian** | Best-in-class DDC/CI + WMI brightness from tray; hotkeys, time-of-day adjustment | Brightness only; DDC reliability varies with docks/DisplayLink/monitor firmware ([overview](https://windowsforum.com/threads/twinkle-tray-control-external-monitor-brightness-on-windows-with-ddc-ci.388272/)); Electron-based, non-trivial RAM |
| **PowerToys FancyZones + Workspaces** ([issues](https://github.com/microsoft/PowerToys/issues/34570)) | Zone layouts per monitor; Workspaces relaunches app sets onto monitors | Workspaces is app-layout only — no resolution/topology/HDR control; unreliable placement on mixed-DPI/mixed-resolution setups ([#39749](https://github.com/microsoft/PowerToys/issues/39749), [#43545](https://github.com/microsoft/PowerToys/issues/43545)); editor can open off-screen ([#36749](https://github.com/microsoft/PowerToys/issues/36749)) |
| **Windows 11 built-in** | 24H2 overhauled HDR settings page, improved DisplayID handling and Auto HDR/VRR latency ([review](https://windowsforum.com/threads/windows-11-24h2-update-review-hdr-enhancements-power-management-and-taskbar-tweaks.364730/)); DRR; 25H2-era updates add >1000 Hz refresh support ([Neowin](https://www.neowin.net/news/windows-11-25h224h2-get-1000-hz-refresh-rate-support-file-explorer-improvements-and-more/)) | Still **no display profiles at all**, no per-app triggers, Win+P only covers duplicate/extend, no external-monitor brightness in Quick Settings, HDR toggle buried per display |

**The composite gap Vantage can own:** no single tool combines (a) reliable topology profiles, (b) HDR/SDR-white-level/refresh/DPI as first-class profile members, (c) DDC/CI brightness+input, (d) window/taskbar layout restore, and (e) per-game automation — behind a native Win11-styled, tray-first, fast UI. Every incumbent does one or two of these, usually with a dated UI or fragile matching.

---

## 3. Modern Windows platform APIs (Windows 11 era)

### 3.1 CCD API — topology save/restore

- Core loop: `GetDisplayConfigBufferSizes` → [`QueryDisplayConfig`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) (use `QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE` for capture) → persist paths+modes → [`SetDisplayConfig`](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/winuser/nf-winuser-setdisplayconfig.md) with `SDC_VALIDATE` first, then `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`. Microsoft's own scenario guidance: [SetDisplayConfig summary and scenarios](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/setdisplayconfig-summary-and-scenarios) and [QueryDisplayConfig scenarios](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/querydisplayconfig-summary-and-scenarios).
- Path array order = path priority; `SDC_TOPOLOGY_*` flags let you recall the CCD database (clone/extend) without supplying modes.
- **Monitor identification:** `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME` returns `DISPLAYCONFIG_TARGET_DEVICE_NAME` (EDID manufacture ID, product code, connector type, `monitorDevicePath`, friendly name). Persist the EDID triple + device path; **adapter LUIDs are not stable across reboots/driver restarts** and must be re-resolved at load time. `ChangeDisplaySettingsEx` is legacy — do not build on it.

### 3.2 HDR

- **Pre-24H2 (still needed as fallback):** `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` / `SET_ADVANCED_COLOR_STATE` — toggles "advanced color" (HDR) per target.
- **Windows 11 24H2+:** new [`DisplayConfigSetDeviceInfo`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-displayconfigsetdeviceinfo) types `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE` and `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` — these distinguish real HDR from ACM (Advanced Color Management on SDR displays), which the old API conflated. Real-world dual-path implementations to copy: [Kodi's 24H2 HDR-toggle PR #26096](https://github.com/xbmc/xbmc/pull/26096), [mpv issue #14567](https://github.com/mpv-player/mpv/issues/14567) (ACM-enabled SDR displays confused the old API), and the free [WKD HDR Switch](https://wkd.gg/utilities/hdr-switch/) per-monitor toggle. **Vantage must implement both paths and detect the OS build.**
- **SDR white level:** get via documented [`DISPLAYCONFIG_SDR_WHITE_LEVEL`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_sdr_white_level) (`SDRWhiteLevel = nits/80*1000`). A matching `DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL` exists but is only semi-documented — see working C code in [ledoge/set_maxtml](https://github.com/ledoge/set_maxtml/blob/master/main_sdrwhite.c). Treat as "undocumented but widely used"; wrap defensively.
- **Auto HDR:** no public API. Global toggle and per-app opt-in live in registry (`HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, `AutoHDREnable=...`); per-game forcing demonstrated by [ledoge/autohdr_force](https://github.com/ledoge/autohdr_force) and [ForceAutoHDR](https://github.com/7gxycn08/ForceAutoHDR). Ship as an "experimental" feature with clear caveats.

### 3.3 WinRT display namespaces (desktop-usable?)

- **`Windows.Devices.Display.DisplayMonitor`** — yes, usable from any desktop app (WinRT activation from .NET works fine). Best modern source for monitor metadata: EDID descriptors, physical size, connection kind, native resolution. Use it to enrich the CCD-derived identity.
- **[`Windows.Devices.Display.Core`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager?view=winrt-26100)** — a low-level API "for third-party compositors"; it takes *exclusive ownership* of display targets to present frames directly (HMD/specialized-display scenarios, see [custom compositor docs](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/specialized-monitors-compositor)). **Not** the right tool for a display manager's set-resolution/HDR use case — stick to CCD.
- **`Windows.Graphics.Display.DisplayInformation`** — per-view API; from Win32 apps obtainable via `IDisplayInformationStaticsInterop::GetForWindow`. Exposes [`AdvancedColorInfo.SdrWhiteLevelInNits`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.display.advancedcolorinfo.sdrwhitelevelinnits?view=winrt-26100) and HDR-capability change events — useful for *reacting* to changes, not for setting them.

### 3.4 Per-monitor DPI scaling (programmatic)

- There is **no documented set-API**. The de facto standard is undocumented `DisplayConfigGetDeviceInfo`/`SetDeviceInfo` types `-3` (get DPI) and `-4` (set DPI), which manipulate the same per-monitor scaling the Settings app writes to `HKCU\Control Panel\Desktop\PerMonitorSettings\<monitorId>`. Reference implementations: [lihas/windows-DPI-scaling-sample](https://github.com/lihas/windows-DPI-scaling-sample) and the CLI tool [imniko/SetDPI](https://github.com/imniko/SetDPI). Note DPI is a *source* property but the undocumented API keys it by adapterId+**targetId**.
- Stable across Win10→Win11 24H2 in practice (DisplayMagician, SetDPI and many tools rely on it), but it is undocumented: isolate behind an interface, feature-flag it, and fail soft.

### 3.5 DDC/CI (Dxva2) — brightness, input, power

- APIs: `GetPhysicalMonitorsFromHMONITOR` → [`GetVCPFeatureAndVCPFeatureReply`](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply) / [`SetVCPFeature`](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature) (docs say ~50 ms per call; `CapabilitiesRequestAndCapabilitiesReply` can take seconds — cache capability strings).
- Key VCP codes: `0x10` luminance, `0x12` contrast, `0x60` input select (e.g. 15=DP1, 17=HDMI1, values vary per monitor — [NirSoft article](https://www.nirsoft.net/articles/set_monitor_input_source_command_line.html)), `0xD6` power mode (soft off/on).
- Real-world reliability (learn from Twinkle Tray): monitors NACK randomly → retry with backoff; serialize commands per monitor; DDC often unavailable through DisplayLink/docks; some monitors need DDC/CI enabled in the OSD; combine with WMI (`WmiMonitorBrightnessMethods`) for laptop panels ([Twinkle Tray](https://github.com/xanderfrangos/twinkle-tray) does DDC+WMI).

### 3.6 Virtual displays (brief)

- Modern route is a user-mode **IddCx indirect display driver**; the dominant open implementation is [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (arbitrary resolutions/refresh, HDR-capable, used by Sunshine/OBS/VR communities). Runs in session 0; crashes don't take down the system. Distribution requires driver signing (attestation-signed via a Hardware Dev Center account) — a heavy lift. Recommendation: out of scope for Vantage v1; optionally integrate with the existing driver if detected.

### 3.7 GPU vendor SDKs

| Vendor | SDK | State (2025-26) | .NET story | Licensing/redistribution |
|---|---|---|---|---|
| NVIDIA | **NVAPI** (`nvapi64.dll` ships with every driver) | Actively updated (R550+ SDKs); Surround = **Mosaic API** (`NvAPI_Mosaic_*`), still fully exposed — this is how DisplayMagician does Surround | No official binding. [NvAPIWrapper](https://github.com/falahati/NvAPIWrapper) (LGPL, covers [Mosaic phase 1+2](https://github.com/falahati/NvAPIWrapper/blob/master/NvAPIWrapper/Native/MosaicApi.cs)) is stale (~2021, pre-Ada structs); DisplayMagician rolled its own P/Invoke ([NVIDIAInfo](https://github.com/terrymacdonald/NVIDIAInfo)) | SDK headers are proprietary; the [NVAPI SDK license](https://www.scribd.com/document/408319915/NVAPI-SDKs-Samples-and-Tools-License-Agreement-Public) prohibits terms that would force NVAPI itself under an OSS license — P/Invoking the driver-installed DLL from an MIT app is the accepted community pattern (don't vendor SDK headers verbatim into a copyleft repo) |
| AMD | **ADLX** (successor; ADL archived but still shipped) | ADLX is the go-forward API; Eyefinity exposed via [`IADLXEyefinityDesktop`](https://gpuopen.com/manuals/adlx/adlx-_d_o_x__i_a_d_l_x_eyefinity_desktop__get_display/); some legacy SLS operations still ADL-only ([community thread](https://community.amd.com/t5/gpu-developer-tools/issue-using-adl-adl2-display-slsmapconfigx2-get-and-adl2-display/m-p/482400)) | Official [C# bindings via SWIG](https://gpuopen.com/manuals/adlx/adlx-page_guide_bindcsharp/) + [C# samples](https://gpuopen.com/manuals/adlx/adlx-page_sample_cs/) — must be built yourself | ADLX SDK on GPUOpen, permissive (MIT-style) headers; runtime ships with Adrenalin driver |
| Intel | **IGCL** ([intel/drivers.gpu.control-library](https://github.com/intel/drivers.gpu.control-library)) | Replaces the OEM-only CUI SDK; covers display, color, sharpness, etc. ([Display API guide](https://intel.github.io/drivers.gpu.control-library/Control/PROG_display.html)); "Intel Combined Display" (Arc multi-monitor spanning) goes through it | C API — P/Invoke needed; header/wrapper repo is MIT, runtime DLL ships with the Intel driver | MIT wrappers, easiest of the three |

Practical takeaway: build a thin `IGpuVendorService` abstraction with P/Invoke backends per vendor, loaded only when that vendor's driver DLL is present; treat vendor spanning (Surround/Eyefinity) as a v1.x feature, since it is the single largest source of incumbent bugs.

---

## 4. UI/app stack for a native Win11 look (2025-2026 reality check)

### 4.1 Options

**WinUI 3 / Windows App SDK**
- Best pixel-perfect Win11 fidelity (Mica, real WinUI controls). But: **no built-in tray icon** — requires Win32 interop or [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) / [SystemTrayWinUI3](https://github.com/MEHDIMYADI/SystemTrayWinUI3), and tray context menus end up non-standard ([issue #6723](https://github.com/microsoft/microsoft-ui-xaml/issues/6723), [Domenic Denicola's "Windows native app development is a mess"](https://domenic.me/windows-native-dev/)).
- Single-file exe is possible only unpackaged+self-contained (WASDK 1.5+), and it extracts to temp on first run, hurting cold start ([deployment docs](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps), [single-exe writeup](https://johnnys.news/2024/03/Revisited-WinUI-publishing-a-single-exe/)). Cold start and memory remain heavier than WPF; tooling (hot reload, designer) still weaker; WASDK ships breaking-ish changes frequently. Notably, DisplayMagician has been "planning" its WinUI3 rewrite for years without shipping it.

**WPF (.NET 8/10) + Fluent library** ⭐ recommended
- .NET 9 added a built-in Fluent/Win11 theme but it is **experimental and buggy**: crashes on OS theme change ([dotnet/wpf #9906](https://github.com/dotnet/wpf/issues/9906)), broken on Windows 10 ([#10096](https://github.com/dotnet/wpf/issues/10096)), ToolBar styling issues ([#9938](https://github.com/dotnet/wpf/issues/9938)); fixes are being staged through .NET 10 ([plan discussion](https://github.com/dotnet/wpf/discussions/10387)). Don't rely on it yet.
- Mature third-party Fluent libraries close the gap today: [WPF-UI (lepo.co)](https://github.com/lepoco/wpfui) (ships as a Visual Studio template, huge adoption) and [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern) (Fluent 2, closest WinUI visual parity, pure WPF, Mica/backdrop support). Tray: [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) (actively maintained continuation of hardcodet's NotifyIcon; native light/dark context menus, efficiency mode).
- WPF strengths for this project: rock-solid P/Invoke interop story, fast cold start with ReadyToRun, framework-dependent builds ~15 MB, 20 years of stability, per-monitor DPI awareness V2 supported. Weakness: no NativeAOT.

**Avalonia 11**
- FluentTheme + [Mica support on Windows 11](https://docs.avaloniaui.net/docs/platform-specific-guides/windows), built-in `TrayIcon` API, NativeAOT/single-file friendly, cross-platform future. With [FluentAvalonia](https://github.com/amwx/FluentAvalonia) it gets close to WinUI visuals but controls are custom-drawn — subtle non-native feel (context menus, text rendering). Solid second choice, best if Linux support ever matters.

**Tauri / Electron**
- Electron: 100+ MB, slowest cold start, highest RAM — Twinkle Tray (Electron) is regularly criticized for exactly this. Tauri is far lighter (WebView2) but Vantage's core is 90% Win32/COM/vendor-SDK interop; putting that behind a Rust/JS IPC boundary adds friction with no cross-platform payoff (the domain is Windows-only). Not recommended.

### 4.2 Recommendation

**WPF on .NET 10 LTS + iNKORE.UI.WPF.Modern (or WPF-UI) + H.NotifyIcon**, architected with the display engine in a separate UI-agnostic library (`Vantage.Core`) so a later WinUI 3 or Avalonia shell swap is cheap.

Rationale: tray-first + fast cold start + heavy P/Invoke are WPF's home turf; the Fluent libraries deliver a convincing Win11 look today (Mica, Fluent 2 controls, dark mode) without WASDK's churn; single-file framework-dependent publishing keeps the download small. Elevation: none of the CCD/HDR/DDC/DPI APIs require admin (registry writes are HKCU), so Vantage can run unelevated — which also keeps MSIX viable later. Choose **Inno Setup (or Velopack) over MSIX** for v1: MSIX cannot self-elevate, complicates auto-update flows outside the Store, and its containerization causes registry/file virtualization surprises ([MSIX limitations](https://www.turbo.net/blog/posts/2025-06-16-understanding-msix-limitations-enterprise-application-compatibility), [elevation thread](https://techcommunity.microsoft.com/discussions/msix-discussions/single-msix-package-containing-two-parts-requiringnot-requiring-administrator-pr/4375762)); Inno remains the community standard for exactly this class of utility ([comparison](https://www.advancedinstaller.com/choosing-the-right-windows-packaging-tool-as-developer.html)).

---

## 5. Distribution & update patterns

- **winget**: submit a manifest PR to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs); installer must be exe/MSI/MSIX (or portable) and **support silent install** ([manifest docs](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest), [submission process](https://learn.microsoft.com/en-us/windows/package-manager/package/repository)). Automate version bumps in CI with [wingetcreate](https://github.com/microsoft/winget-create). Signing is not mandated by winget itself, but unsigned installers still trip SmartScreen when downloaded directly.
- **Auto-update — [Velopack](https://velopack.io/)**: the successor to Squirrel/Clowd.Squirrel (same author, core rewritten in Rust). Free/OSS, zero-UAC installs to `%LocalAppData%`, delta updates, ~2-second apply-and-relaunch, and — critically for a tray app — it keeps the exe path stable across updates, avoiding broken firewall rules, GPU preferences and **tray icon pinning** that plagued Squirrel ([docs](https://docs.velopack.io/), [repo](https://github.com/velopack/velopack)). First-class .NET SDK; GitHub Releases can serve as the update feed. Recommended: Velopack for the default download + a winget manifest tracking each release (winget users update via winget; suppress Velopack auto-update when installed through winget if desired).
- **Code signing (the hard part for OSS)**:
  - Azure Trusted Signing (being renamed **Azure Artifact Signing**) looked like the cheap answer ($9.99/mo), but **individual-developer onboarding is paused** and new sign-ups are restricted to US/Canada organizations with 3+ years of verifiable history ([Microsoft's code-signing options page](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options), [Q&A confirming the pause](https://learn.microsoft.com/en-nz/answers/questions/5810735/cant-create-a-new-trusted-signing-individual-ident), [original individual-preview announcement](https://techcommunity.microsoft.com/blog/microsoft-security-blog/trusted-signing-is-now-open-for-individual-developers-to-sign-up-in-public-previ/4273554)).
  - **[SignPath Foundation](https://signpath.io/solutions/open-source-community)** offers free OV-level signing for qualifying open-source projects via a managed CI pipeline — the most realistic path for Vantage as OSS (used by many Windows utilities).
  - Fallback: commercial OV certificate (~$200-400/yr, HSM/token required since 2023) or ship unsigned initially and rely on winget + SmartScreen reputation accrual; MSIX would *require* signing, another reason to defer it.
- **Release hygiene borrowed from incumbents' mistakes**: keep profile store format versioned and migrated automatically on update (DisplayMagician broke profiles at v2.4 and at the Win10→11 boundary); publish portable zip alongside installer (power users of NirSoft-style tools expect it).

---

## Appendix: source index (primary)

- DisplayMagician repo/issues/wiki: https://github.com/terrymacdonald/DisplayMagician (+ issues #7, #52, #131, #254, #307, #316, #348, #351, #361, #370, #386, #398, #401)
- HeliosDisplayManagement: https://github.com/falahati/HeliosDisplayManagement
- CCD API: https://learn.microsoft.com/en-us/windows-hardware/drivers/display/setdisplayconfig-summary-and-scenarios
- 24H2 HDR API migration: https://github.com/xbmc/xbmc/pull/26096
- SDR white level set (undocumented): https://github.com/ledoge/set_maxtml
- DPI undocumented API: https://github.com/lihas/windows-DPI-scaling-sample, https://github.com/imniko/SetDPI
- DDC/CI: https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature
- Vendor SDKs: https://github.com/falahati/NvAPIWrapper, https://gpuopen.com/manuals/adlx/adlx-page_guide_bindcsharp/, https://github.com/intel/drivers.gpu.control-library
- WPF Fluent status: https://github.com/dotnet/wpf/discussions/10387
- Velopack: https://docs.velopack.io/ · SignPath OSS: https://signpath.io/solutions/open-source-community
