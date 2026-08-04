using System.Runtime.InteropServices;

namespace Vantage.Interop.Nvidia;

/// <summary>
/// Minimal owned NVAPI binding (BLUEPRINT P5: source-only, no vendored wrapper DLLs, loaded
/// only when the NVIDIA driver is present). Covers exactly what the Windows API cannot do:
/// reading and setting the output color depth (bpc) per display.
///
/// Technique: nvapi64.dll exports one symbol, nvapi_QueryInterface; every function is
/// resolved from a 32-bit hash id and versioned structs carry (size | version&lt;&lt;16).
/// Ids and layouts verified against NVIDIA's public nvapi.h (R410+ stable).
/// </summary>
public static class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdDispColorControl = 0x92F9D80D;
    private const uint IdGetDisplayIdByDisplayName = 0xAE457190;

    private const byte ColorCmdGet = 1;
    private const byte ColorCmdSet = 2;
    private const uint SelectionPolicyUser = 0;

    /// <summary>NV_COLOR_DATA_V5 — 24 bytes. Field order per nvapi.h: format, colorimetry, dynamic range, depth, policy, desktop depth.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct NV_COLOR_DATA_V5
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public ushort Size;
        [FieldOffset(6)] public byte Cmd;
        [FieldOffset(8)] public byte ColorFormat;
        [FieldOffset(9)] public byte Colorimetry;
        [FieldOffset(10)] public byte DynamicRange;
        [FieldOffset(12)] public uint Depth;             // ColorDataDepth: 0=default 1=6bpc 2=8bpc 3=10bpc 4=12bpc 5=16bpc
        [FieldOffset(16)] public uint SelectionPolicy;   // 0=user 1=best quality
        [FieldOffset(20)] public uint DesktopDepth;

        public static NV_COLOR_DATA_V5 Create(byte cmd) => new()
        {
            Version = 24 | (5u << 16),
            Size = 24,
            Cmd = cmd,
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDisplayIdByDisplayNameDelegate([MarshalAs(UnmanagedType.LPStr)] string displayName, ref uint displayId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DispColorControlDelegate(uint displayId, ref NV_COLOR_DATA_V5 colorData);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var init = GetDelegate<InitializeDelegate>(IdInitialize);
            return init is not null && init() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    private static GetDisplayIdByDisplayNameDelegate? _getDisplayId;
    private static DispColorControlDelegate? _colorControl;

    public static bool IsAvailable => Available.Value;

    private static T? GetDelegate<T>(uint id) where T : class
    {
        var ptr = QueryInterface(id);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>Resolves the NVAPI display id for a GDI device name (\\.\DISPLAY1). False for non-NVIDIA outputs.</summary>
    public static bool TryGetDisplayId(string gdiDeviceName, out uint displayId)
    {
        displayId = 0;
        if (!IsAvailable || string.IsNullOrEmpty(gdiDeviceName))
            return false;
        _getDisplayId ??= GetDelegate<GetDisplayIdByDisplayNameDelegate>(IdGetDisplayIdByDisplayName);
        return _getDisplayId is not null && _getDisplayId(gdiDeviceName, ref displayId) == 0;
    }

    /// <summary>Current output color depth in bits per channel; null when unknown or driver-default.</summary>
    public static int? GetOutputBpc(uint displayId)
    {
        if (!IsAvailable)
            return null;
        _colorControl ??= GetDelegate<DispColorControlDelegate>(IdDispColorControl);
        if (_colorControl is null)
            return null;

        var data = NV_COLOR_DATA_V5.Create(ColorCmdGet);
        if (_colorControl(displayId, ref data) != 0)
            return null;
        return DepthToBpc(data.Depth);
    }

    /// <summary>
    /// Sets the output color depth (get-modify-set so format/colorimetry are preserved).
    /// The caller must verify with <see cref="GetOutputBpc"/> — the driver may refuse
    /// combinations the display can't do at the current mode.
    /// </summary>
    public static bool SetOutputBpc(uint displayId, int bpc)
    {
        if (!IsAvailable || BpcToDepth(bpc) is not { } depth)
            return false;
        _colorControl ??= GetDelegate<DispColorControlDelegate>(IdDispColorControl);
        if (_colorControl is null)
            return false;

        var data = NV_COLOR_DATA_V5.Create(ColorCmdGet);
        if (_colorControl(displayId, ref data) != 0)
            return false;

        data.Cmd = ColorCmdSet;
        data.Depth = depth;
        data.SelectionPolicy = SelectionPolicyUser;
        return _colorControl(displayId, ref data) == 0;
    }

    private static int? DepthToBpc(uint depth) => depth switch
    {
        1 => 6,
        2 => 8,
        3 => 10,
        4 => 12,
        5 => 16,
        _ => null, // 0 = driver default → unknown actual value at this layer
    };

    private static uint? BpcToDepth(int bpc) => bpc switch
    {
        6 => 1,
        8 => 2,
        10 => 3,
        12 => 4,
        16 => 5,
        _ => null,
    };
}
