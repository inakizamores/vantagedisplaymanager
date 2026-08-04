using System.Runtime.InteropServices;

namespace Vantage.Interop.Ccd;

/// <summary>Result of a CCD call that can fail without being exceptional.</summary>
public readonly record struct CcdResult<T>(int Error, T? Value)
{
    public bool Succeeded => Error == 0;
    public static CcdResult<T> Ok(T value) => new(0, value);
    public static CcdResult<T> Fail(int error) => new(error, default);
}

/// <summary>
/// Safe wrapper over the CCD API. Stateless; every method reflects live OS state.
/// Setters are unreliable by contract (the OS may report success and not apply) —
/// callers must verify by re-querying. See BLUEPRINT.md P2/P4.
/// </summary>
public static class CcdApi
{
    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;

    /// <summary>Standard Windows DPI scale steps; relative DPI offsets index into this table.</summary>
    public static readonly int[] DpiScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>
    /// Captures the active display paths and modes, retrying on the documented
    /// ERROR_INSUFFICIENT_BUFFER race when topology changes between the two calls.
    /// </summary>
    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryActive(
        QueryDisplayFlags flags = QueryDisplayFlags.OnlyActivePaths | QueryDisplayFlags.VirtualModeAware)
    {
        for (var attempt = 0; ; attempt++)
        {
            var err = NativeMethods.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (err != ErrorSuccess)
                throw new CcdException("GetDisplayConfigBufferSizes", err);

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = NativeMethods.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (err == ErrorInsufficientBuffer && attempt < 3)
                continue; // topology changed between the calls; retry with fresh sizes

            if (err != ErrorSuccess)
                throw new CcdException("QueryDisplayConfig", err);

            if (pathCount != paths.Length) Array.Resize(ref paths, (int)pathCount);
            if (modeCount != modes.Length) Array.Resize(ref modes, (int)modeCount);
            return (paths, modes);
        }
    }

    public static int Validate(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, bool virtualModeAware = true)
    {
        var flags = SetDisplayConfigFlags.Validate
                  | SetDisplayConfigFlags.UseSuppliedDisplayConfig
                  | SetDisplayConfigFlags.AllowChanges;
        if (virtualModeAware) flags |= SetDisplayConfigFlags.VirtualModeAware;
        return NativeMethods.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
    }

    public static int Apply(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, bool virtualModeAware = true)
    {
        var flags = SetDisplayConfigFlags.Apply
                  | SetDisplayConfigFlags.UseSuppliedDisplayConfig
                  | SetDisplayConfigFlags.AllowChanges
                  | SetDisplayConfigFlags.SaveToDatabase;
        if (virtualModeAware) flags |= SetDisplayConfigFlags.VirtualModeAware;
        return NativeMethods.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
    }

    /// <summary>Fallback: supply only the topology and let Windows pick the modes.</summary>
    public static int ApplyTopologyOnly(DISPLAYCONFIG_PATH_INFO[] paths)
        => NativeMethods.SetDisplayConfig((uint)paths.Length, paths, 0, null,
            SetDisplayConfigFlags.Apply | SetDisplayConfigFlags.TopologySupplied | SetDisplayConfigFlags.AllowChanges);

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header<T>(DISPLAYCONFIG_DEVICE_INFO_TYPE type, LUID adapterId, uint id)
        where T : struct
        => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapterId, Id = id };

    public static CcdResult<DISPLAYCONFIG_SOURCE_DEVICE_NAME> GetSourceName(LUID adapterId, uint sourceId)
    {
        var packet = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = Header<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceName, adapterId, sourceId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_SOURCE_DEVICE_NAME>.Ok(packet) : CcdResult<DISPLAYCONFIG_SOURCE_DEVICE_NAME>.Fail(err);
    }

    public static CcdResult<DISPLAYCONFIG_TARGET_DEVICE_NAME> GetTargetName(LUID adapterId, uint targetId)
    {
        var packet = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            Header = Header<DISPLAYCONFIG_TARGET_DEVICE_NAME>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName, adapterId, targetId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_TARGET_DEVICE_NAME>.Ok(packet) : CcdResult<DISPLAYCONFIG_TARGET_DEVICE_NAME>.Fail(err);
    }

    public static CcdResult<DISPLAYCONFIG_ADAPTER_NAME> GetAdapterName(LUID adapterId)
    {
        var packet = new DISPLAYCONFIG_ADAPTER_NAME
        {
            Header = Header<DISPLAYCONFIG_ADAPTER_NAME>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdapterName, adapterId, 0),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_ADAPTER_NAME>.Ok(packet) : CcdResult<DISPLAYCONFIG_ADAPTER_NAME>.Fail(err);
    }

    public static CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO> GetAdvancedColorInfo(LUID adapterId, uint targetId)
    {
        var packet = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            Header = Header<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdvancedColorInfo, adapterId, targetId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>.Ok(packet) : CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>.Fail(err);
    }

    /// <summary>Windows 11 24H2+ only; returns the OS error (probably ERROR_INVALID_PARAMETER) on older builds.</summary>
    public static CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2> GetAdvancedColorInfo2(LUID adapterId, uint targetId)
    {
        var packet = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            Header = Header<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdvancedColorInfo2, adapterId, targetId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>.Ok(packet) : CcdResult<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>.Fail(err);
    }

    public static CcdResult<DISPLAYCONFIG_SDR_WHITE_LEVEL> GetSdrWhiteLevel(LUID adapterId, uint targetId)
    {
        var packet = new DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            Header = Header<DISPLAYCONFIG_SDR_WHITE_LEVEL>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSdrWhiteLevel, adapterId, targetId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_SDR_WHITE_LEVEL>.Ok(packet) : CcdResult<DISPLAYCONFIG_SDR_WHITE_LEVEL>.Fail(err);
    }

    public static int SetAdvancedColorState(LUID adapterId, uint targetId, bool enable)
    {
        var packet = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            Header = Header<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(DISPLAYCONFIG_DEVICE_INFO_TYPE.SetAdvancedColorState, adapterId, targetId),
            Value = enable ? 1u : 0u,
        };
        return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
    }

    /// <summary>Windows 11 24H2+ only.</summary>
    public static int SetHdrState(LUID adapterId, uint targetId, bool enable)
    {
        var packet = new DISPLAYCONFIG_SET_HDR_STATE
        {
            Header = Header<DISPLAYCONFIG_SET_HDR_STATE>(DISPLAYCONFIG_DEVICE_INFO_TYPE.SetHdrState, adapterId, targetId),
            EnableHdr = enable,
        };
        return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
    }

    /// <summary>Undocumented API. DPI rides the CCD source, keyed here by adapter + source id.</summary>
    public static CcdResult<DISPLAYCONFIG_GET_SOURCE_DPI_SCALE> GetSourceDpiScale(LUID adapterId, uint sourceId)
    {
        var packet = new DISPLAYCONFIG_GET_SOURCE_DPI_SCALE
        {
            Header = Header<DISPLAYCONFIG_GET_SOURCE_DPI_SCALE>(DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceDpiScale, adapterId, sourceId),
        };
        var err = NativeMethods.DisplayConfigGetDeviceInfo(ref packet);
        return err == 0 ? CcdResult<DISPLAYCONFIG_GET_SOURCE_DPI_SCALE>.Ok(packet) : CcdResult<DISPLAYCONFIG_GET_SOURCE_DPI_SCALE>.Fail(err);
    }

    /// <summary>Undocumented API. <paramref name="scaleRel"/> is steps relative to the recommended scale.</summary>
    public static int SetSourceDpiScale(LUID adapterId, uint sourceId, int scaleRel)
    {
        var packet = new DISPLAYCONFIG_SET_SOURCE_DPI_SCALE
        {
            Header = Header<DISPLAYCONFIG_SET_SOURCE_DPI_SCALE>(DISPLAYCONFIG_DEVICE_INFO_TYPE.SetSourceDpiScale, adapterId, sourceId),
            ScaleRel = scaleRel,
        };
        return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
    }

    /// <summary>Undocumented API. <paramref name="nits"/> typically 80-480; Windows stores nits*1000/80.</summary>
    public static int SetSdrWhiteLevel(LUID adapterId, uint targetId, double nits)
    {
        var packet = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
        {
            Header = Header<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL>(DISPLAYCONFIG_DEVICE_INFO_TYPE.SetSdrWhiteLevel, adapterId, targetId),
            SDRWhiteLevel = (uint)Math.Round(nits * 1000.0 / 80.0),
            FinalValue = 1,
        };
        return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
    }

    public static double SdrWhiteLevelToNits(uint sdrWhiteLevel) => sdrWhiteLevel * 80.0 / 1000.0;
}

public sealed class CcdException(string function, int win32Error)
    : Exception($"{function} failed with Win32 error {win32Error} ({new System.ComponentModel.Win32Exception(win32Error).Message})")
{
    public string Function { get; } = function;
    public int Win32Error { get; } = win32Error;
}
