using Microsoft.Win32;

namespace Vantage.Interop.Edid;

public sealed record EdidInfo(
    string ManufacturerCode,   // e.g. "SAM"
    ushort ProductCode,        // e.g. 0x7454
    uint SerialNumber,         // numeric serial from the base block
    string? SerialText,        // descriptor-block serial string (more unique when present)
    string? DisplayName,       // descriptor-block model name
    int PhysicalWidthMm,
    int PhysicalHeightMm)
{
    /// <summary>Stable cross-session identity: vendor + product + best-available serial.</summary>
    public string StableId =>
        $"{ManufacturerCode}{ProductCode:X4}_{(string.IsNullOrWhiteSpace(SerialText) ? SerialNumber.ToString() : SerialText.Trim())}";
}

/// <summary>
/// Reads and parses monitor EDID blobs from the PnP registry
/// (SYSTEM\CurrentControlSet\Enum\DISPLAY\...\Device Parameters\EDID). Read-only, no admin needed.
/// </summary>
public static class EdidReader
{
    /// <summary>
    /// Converts a CCD monitor device path
    /// (\\?\DISPLAY#SAM7454#5&amp;35454913&amp;0&amp;UID4355#{guid}) to a PnP device instance ID
    /// (DISPLAY\SAM7454\5&amp;35454913&amp;0&amp;UID4355).
    /// </summary>
    public static string? DevicePathToInstanceId(string monitorDevicePath)
    {
        if (string.IsNullOrEmpty(monitorDevicePath))
            return null;

        var s = monitorDevicePath;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
            s = s[4..];

        var parts = s.Split('#');
        if (parts.Length < 3)
            return null;

        return string.Join('\\', parts[0], parts[1], parts[2]);
    }

    public static EdidInfo? TryReadFromDevicePath(string monitorDevicePath)
    {
        var instanceId = DevicePathToInstanceId(monitorDevicePath);
        return instanceId is null ? null : TryReadFromInstanceId(instanceId);
    }

    public static EdidInfo? TryReadFromInstanceId(string deviceInstanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{deviceInstanceId}\Device Parameters", writable: false);
            if (key?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                return Parse(edid);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through — caller degrades to NOEDID_* identity.
        }
        return null;
    }

    public static EdidInfo? Parse(ReadOnlySpan<byte> edid)
    {
        // Base block sanity: 00 FF FF FF FF FF FF 00 header.
        if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF || edid[7] != 0x00)
            return null;

        // Bytes 8-9: big-endian packed 3x5-bit manufacturer letters ('A' = 1).
        var packed = (edid[8] << 8) | edid[9];
        Span<char> mfr =
        [
            (char)('A' + ((packed >> 10) & 0x1F) - 1),
            (char)('A' + ((packed >> 5) & 0x1F) - 1),
            (char)('A' + (packed & 0x1F) - 1),
        ];

        var productCode = (ushort)(edid[10] | (edid[11] << 8));
        var serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        // Physical size in cm at bytes 21/22; detailed timing block carries mm fallback.
        var widthMm = edid[21] * 10;
        var heightMm = edid[22] * 10;

        string? serialText = null;
        string? displayName = null;
        for (var offset = 54; offset + 18 <= 128; offset += 18)
        {
            var block = edid.Slice(offset, 18);
            if (block[0] != 0 || block[1] != 0 || block[2] != 0)
            {
                // Detailed timing descriptor — refine physical size from mm fields.
                var w = block[12] | ((block[14] & 0xF0) << 4);
                var h = block[13] | ((block[14] & 0x0F) << 8);
                if (w > 0 && h > 0)
                {
                    widthMm = w;
                    heightMm = h;
                }
                continue;
            }

            switch (block[3])
            {
                case 0xFF: serialText = ReadDescriptorText(block); break;
                case 0xFC: displayName = ReadDescriptorText(block); break;
            }
        }

        return new EdidInfo(new string(mfr), productCode, serial, serialText, displayName, widthMm, heightMm);
    }

    private static string ReadDescriptorText(ReadOnlySpan<byte> block)
    {
        Span<char> chars = stackalloc char[13];
        var len = 0;
        for (var i = 5; i < 18; i++)
        {
            if (block[i] == 0x0A)
                break;
            chars[len++] = (char)block[i];
        }
        return new string(chars[..len]).Trim();
    }
}
