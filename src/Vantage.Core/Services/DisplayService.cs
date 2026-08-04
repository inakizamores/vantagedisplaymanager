using System.Runtime.InteropServices;
using Vantage.Core.Models;
using Vantage.Interop;
using Vantage.Interop.Ccd;
using Vantage.Interop.Edid;

namespace Vantage.Core.Services;

/// <summary>
/// Captures the live display configuration into the normalized model + raw replay payload.
/// Pure reads — never mutates system state.
/// </summary>
public sealed class DisplayService
{
    public SystemSnapshot Capture()
    {
        var (paths, modes) = CcdApi.QueryActive();

        var adapterPathCache = new Dictionary<ulong, string>();
        var displays = new List<DisplayState>();

        foreach (var path in paths)
        {
            if ((path.Flags & DISPLAYCONFIG_PATH_INFO.PATH_ACTIVE) == 0)
                continue;

            var sourceLuid = path.SourceInfo.AdapterId;
            var targetLuid = path.TargetInfo.AdapterId;

            var targetName = CcdApi.GetTargetName(targetLuid, path.TargetInfo.Id);
            var sourceName = CcdApi.GetSourceName(sourceLuid, path.SourceInfo.Id);
            var adapterPath = GetAdapterPathCached(adapterPathCache, targetLuid);

            // Identity (P3): device instance ID from the monitor device path, enriched with EDID.
            var devicePath = targetName.Succeeded ? targetName.Value.MonitorDevicePath : string.Empty;
            var instanceId = EdidReader.DevicePathToInstanceId(devicePath) ?? $"UNKNOWN\\{targetLuid.ToUInt64():X}_{path.TargetInfo.Id}";
            var edid = string.IsNullOrEmpty(devicePath) ? null : EdidReader.TryReadFromDevicePath(devicePath);

            var friendlyName = targetName.Succeeded && !string.IsNullOrWhiteSpace(targetName.Value.MonitorFriendlyDeviceName)
                ? targetName.Value.MonitorFriendlyDeviceName
                : edid?.DisplayName;

            var identity = new MonitorIdentity
            {
                StableId = edid?.StableId ?? $"NOEDID_{instanceId.Replace('\\', '_')}",
                DeviceInstanceId = instanceId,
                FriendlyName = friendlyName,
                EdidManufacturer = edid?.ManufacturerCode,
                EdidProductCode = edid?.ProductCode ?? 0,
                EdidSerial = edid?.SerialText ?? edid?.SerialNumber.ToString(),
            };

            // Source mode: position + desktop resolution. Indices are 16-bit halves because
            // we always query with QDC_VIRTUAL_MODE_AWARE.
            DISPLAYCONFIG_SOURCE_MODE? sourceMode = null;
            var srcIdx = path.SourceInfo.SourceModeInfoIdx;
            if (srcIdx != DISPLAYCONFIG_PATH_SOURCE_INFO.PATH_SOURCE_MODE_IDX_INVALID && srcIdx < modes.Length
                && modes[srcIdx].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
                sourceMode = modes[srcIdx].SourceMode;

            DISPLAYCONFIG_VIDEO_SIGNAL_INFO? signal = null;
            var tgtIdx = path.TargetInfo.TargetModeInfoIdx;
            if (tgtIdx != DISPLAYCONFIG_PATH_TARGET_INFO.TARGET_MODE_IDX_INVALID && tgtIdx < modes.Length
                && modes[tgtIdx].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE.Target)
                signal = modes[tgtIdx].TargetMode.TargetVideoSignalInfo;

            var refreshMillihertz = signal is { } s && s.VSyncFreq.Denominator != 0
                ? (uint)Math.Round(s.VSyncFreq.Numerator * 1000.0 / s.VSyncFreq.Denominator)
                : (uint)Math.Round(path.TargetInfo.RefreshRate.ToDouble() * 1000.0);

            // HDR (P4): 24H2 info-2 first, legacy advanced color as fallback.
            var hdr = CaptureHdr(targetLuid, path.TargetInfo.Id);

            // DPI (undocumented; fail soft).
            DpiInfo? dpi = null;
            var dpiScale = CcdApi.GetSourceDpiScale(sourceLuid, path.SourceInfo.Id);
            if (dpiScale.Succeeded)
            {
                var v = dpiScale.Value;
                // curScaleRel is relative to recommended; recommended sits at index -minScaleRel.
                var steps = CcdApi.DpiScaleSteps;
                var recIdx = Math.Clamp(-v.MinScaleRel, 0, steps.Length - 1);
                var curIdx = Math.Clamp(recIdx + v.CurScaleRel, 0, steps.Length - 1);
                var maxIdx = Math.Clamp(recIdx + v.MaxScaleRel, 0, steps.Length - 1);
                dpi = new DpiInfo
                {
                    RecommendedPercent = steps[recIdx],
                    CurrentPercent = steps[curIdx],
                    MinPercent = steps[0],
                    MaxPercent = steps[maxIdx],
                };
            }

            displays.Add(new DisplayState
            {
                Identity = identity,
                Address = new CcdAddress
                {
                    AdapterLuid = targetLuid.ToUInt64(),
                    SourceId = path.SourceInfo.Id,
                    TargetId = path.TargetInfo.Id,
                },
                GdiDeviceName = sourceName.Succeeded ? sourceName.Value.ViewGdiDeviceName : null,
                AdapterDevicePath = adapterPath,
                OutputTechnology = path.TargetInfo.OutputTechnology.ToString(),
                IsPrimary = sourceMode is { Position.X: 0, Position.Y: 0 },
                PositionX = sourceMode?.Position.X ?? 0,
                PositionY = sourceMode?.Position.Y ?? 0,
                Width = sourceMode?.Width ?? 0,
                Height = sourceMode?.Height ?? 0,
                RefreshMillihertz = refreshMillihertz,
                Rotation = (DisplayRotation)(uint)path.TargetInfo.Rotation,
                Scaling = path.TargetInfo.Scaling.ToString(),
                Hdr = hdr,
                Dpi = dpi,
                PhysicalWidthMm = edid?.PhysicalWidthMm ?? 0,
                PhysicalHeightMm = edid?.PhysicalHeightMm ?? 0,
            });
        }

        return new SystemSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Displays = displays,
            Replay = BuildReplayPayload(paths, modes, displays, adapterPathCache),
        };
    }

    private static HdrInfo CaptureHdr(LUID adapterId, uint targetId)
    {
        double? sdrNits = null;
        var white = CcdApi.GetSdrWhiteLevel(adapterId, targetId);
        if (white.Succeeded)
            sdrNits = CcdApi.SdrWhiteLevelToNits(white.Value.SDRWhiteLevel);

        if (WindowsVersion.IsWindows11_24H2OrGreater)
        {
            var info2 = CcdApi.GetAdvancedColorInfo2(adapterId, targetId);
            if (info2.Succeeded)
            {
                var v = info2.Value;
                return new HdrInfo
                {
                    Supported = v.HighDynamicRangeSupported,
                    // Crucial 24H2 distinction: only mode==HDR is really HDR (ACM sets the old bits for SDR too).
                    Enabled = v.ActiveColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE.Hdr,
                    ActiveColorMode = v.ActiveColorMode.ToString(),
                    BitsPerColorChannel = v.BitsPerColorChannel,
                    ColorEncoding = v.ColorEncoding.ToString(),
                    SdrWhiteLevelNits = sdrNits,
                };
            }
        }

        var info = CcdApi.GetAdvancedColorInfo(adapterId, targetId);
        if (info.Succeeded)
        {
            var v = info.Value;
            return new HdrInfo
            {
                Supported = v.AdvancedColorSupported && !v.AdvancedColorForceDisabled,
                Enabled = v.AdvancedColorEnabled,
                BitsPerColorChannel = v.BitsPerColorChannel,
                ColorEncoding = v.ColorEncoding.ToString(),
                SdrWhiteLevelNits = sdrNits,
            };
        }

        return new HdrInfo();
    }

    private static string? GetAdapterPathCached(Dictionary<ulong, string> cache, LUID adapterId)
    {
        var key = adapterId.ToUInt64();
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var result = CcdApi.GetAdapterName(adapterId);
        if (!result.Succeeded)
            return null;
        cache[key] = result.Value.AdapterDevicePath;
        return result.Value.AdapterDevicePath;
    }

    private static ReplayPayload BuildReplayPayload(
        DISPLAYCONFIG_PATH_INFO[] paths,
        DISPLAYCONFIG_MODE_INFO[] modes,
        List<DisplayState> displays,
        Dictionary<ulong, string> adapterPathCache)
    {
        var byTarget = displays.ToDictionary(d => (d.Address.AdapterLuid, d.Address.TargetId), d => d.Identity.StableId);

        var replayPaths = new List<ReplayPath>(paths.Length);
        foreach (var p in paths)
        {
            // Also cache source adapter paths so the LUID map is complete.
            GetAdapterPathCached(adapterPathCache, p.SourceInfo.AdapterId);
            GetAdapterPathCached(adapterPathCache, p.TargetInfo.AdapterId);

            byTarget.TryGetValue((p.TargetInfo.AdapterId.ToUInt64(), p.TargetInfo.Id), out var stableId);
            replayPaths.Add(new ReplayPath
            {
                SourceAdapter = p.SourceInfo.AdapterId.ToUInt64(),
                SourceId = p.SourceInfo.Id,
                SourceModeInfoIdx = p.SourceInfo.ModeInfoIdx,
                SourceStatusFlags = p.SourceInfo.StatusFlags,
                TargetAdapter = p.TargetInfo.AdapterId.ToUInt64(),
                TargetId = p.TargetInfo.Id,
                TargetModeInfoIdx = p.TargetInfo.ModeInfoIdx,
                OutputTechnology = (uint)p.TargetInfo.OutputTechnology,
                Rotation = (uint)p.TargetInfo.Rotation,
                Scaling = (uint)p.TargetInfo.Scaling,
                RefreshNumerator = p.TargetInfo.RefreshRate.Numerator,
                RefreshDenominator = p.TargetInfo.RefreshRate.Denominator,
                ScanLineOrdering = (uint)p.TargetInfo.ScanLineOrdering,
                TargetAvailable = p.TargetInfo.TargetAvailable,
                TargetStatusFlags = p.TargetInfo.StatusFlags,
                Flags = p.Flags,
                MonitorStableId = stableId,
            });
        }

        var replayModes = new List<ReplayMode>(modes.Length);
        foreach (var m in modes)
        {
            var union = new byte[48];
            unsafe
            {
                fixed (byte* dst = union)
                {
                    var tmp = m;
                    Buffer.MemoryCopy((byte*)&tmp + 16, dst, 48, 48);
                }
            }
            replayModes.Add(new ReplayMode
            {
                InfoType = (uint)m.InfoType,
                Id = m.Id,
                Adapter = m.AdapterId.ToUInt64(),
                UnionBytes = Convert.ToBase64String(union),
            });
        }

        return new ReplayPayload
        {
            Paths = replayPaths,
            Modes = replayModes,
            AdapterPaths = adapterPathCache.ToDictionary(kv => kv.Key.ToString("X"), kv => kv.Value),
        };
    }

    /// <summary>Rebuilds native CCD arrays from a replay payload, translating stored adapter LUIDs to <paramref name="luidMap"/>.</summary>
    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) ToNative(
        ReplayPayload replay, IReadOnlyDictionary<ulong, ulong> luidMap)
    {
        static LUID Translate(IReadOnlyDictionary<ulong, ulong> map, ulong stored)
        {
            var v = map.TryGetValue(stored, out var current) ? current : stored;
            return new LUID { LowPart = (uint)(v & 0xFFFFFFFF), HighPart = (int)(v >> 32) };
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[replay.Paths.Count];
        for (var i = 0; i < paths.Length; i++)
        {
            var p = replay.Paths[i];
            paths[i] = new DISPLAYCONFIG_PATH_INFO
            {
                SourceInfo = new DISPLAYCONFIG_PATH_SOURCE_INFO
                {
                    AdapterId = Translate(luidMap, p.SourceAdapter),
                    Id = p.SourceId,
                    ModeInfoIdx = p.SourceModeInfoIdx,
                    StatusFlags = p.SourceStatusFlags,
                },
                TargetInfo = new DISPLAYCONFIG_PATH_TARGET_INFO
                {
                    AdapterId = Translate(luidMap, p.TargetAdapter),
                    Id = p.TargetId,
                    ModeInfoIdx = p.TargetModeInfoIdx,
                    OutputTechnology = (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)p.OutputTechnology,
                    Rotation = (DISPLAYCONFIG_ROTATION)p.Rotation,
                    Scaling = (DISPLAYCONFIG_SCALING)p.Scaling,
                    RefreshRate = new DISPLAYCONFIG_RATIONAL { Numerator = p.RefreshNumerator, Denominator = p.RefreshDenominator },
                    ScanLineOrdering = (DISPLAYCONFIG_SCANLINE_ORDERING)p.ScanLineOrdering,
                    TargetAvailable = p.TargetAvailable,
                    StatusFlags = p.TargetStatusFlags,
                },
                Flags = p.Flags,
            };
        }

        var modes = new DISPLAYCONFIG_MODE_INFO[replay.Modes.Count];
        for (var i = 0; i < modes.Length; i++)
        {
            var m = replay.Modes[i];
            var mode = new DISPLAYCONFIG_MODE_INFO
            {
                InfoType = (DISPLAYCONFIG_MODE_INFO_TYPE)m.InfoType,
                Id = m.Id,
                AdapterId = Translate(luidMap, m.Adapter),
            };
            var union = Convert.FromBase64String(m.UnionBytes);
            unsafe
            {
                fixed (byte* src = union)
                {
                    Buffer.MemoryCopy(src, (byte*)&mode + 16, 48, Math.Min(union.Length, 48));
                }
            }
            modes[i] = mode;
        }

        return (paths, modes);
    }
}
