using System.Runtime.InteropServices;

namespace Vantage.Interop.Ccd;

internal static partial class NativeMethods
{
    private const string User32 = "user32.dll";

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(
        QueryDisplayFlags flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(
        QueryDisplayFlags flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(
        QueryDisplayFlags flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        out DISPLAYCONFIG_TOPOLOGY_ID currentTopologyId);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        SetDisplayConfigFlags flags);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_SOURCE_DPI_SCALE requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_SOURCE_DPI_SCALE requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_SDR_WHITE_LEVEL requestPacket);
}
