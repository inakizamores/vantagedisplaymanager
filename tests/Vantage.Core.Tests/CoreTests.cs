using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;
using Vantage.Core.Services;
using Vantage.Interop.Edid;
using Xunit;

namespace Vantage.Core.Tests;

public class ProfileStoreTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static SystemSnapshot LoadSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-g9-avr.json");
        return JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(path), Json)!;
    }

    [Fact]
    public void SaveLoad_RoundTrips_AndKeepsBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vantage-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ProfileStore(Path.Combine(dir, "profiles.json"));
            var snapshot = LoadSnapshot();

            store.Upsert(ProfileStore.FromSnapshot(snapshot, "First"));
            store.Upsert(ProfileStore.FromSnapshot(snapshot, "Second"));

            var loaded = store.Load();
            Assert.Equal(2, loaded.Profiles.Count);
            Assert.Equal(ProfileStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.True(File.Exists(Path.Combine(dir, "profiles.json.bak")), "atomic write should keep a backup");

            var second = store.Find("second");
            Assert.NotNull(second);
            Assert.Equal("Second", second!.Name);

            Assert.True(store.Delete(second.Id));
            Assert.Single(store.Load().Profiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewerSchema_RefusesToLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vantage-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "profiles.json");
            File.WriteAllText(file, """{"schemaVersion": 999, "profiles": []}""");
            var store = new ProfileStore(file);
            Assert.Throws<InvalidOperationException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ReplayPayloadTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void ToNative_TranslatesAdapterLuids()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-g9-avr.json");
        var snapshot = JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(path), Json)!;

        var storedLuid = snapshot.Replay.Paths[0].SourceAdapter;
        const ulong newLuid = 0xABCDEF12;
        var map = new Dictionary<ulong, ulong> { [storedLuid] = newLuid };

        var (paths, modes) = DisplayService.ToNative(snapshot.Replay, map);

        Assert.Equal(snapshot.Replay.Paths.Count, paths.Length);
        Assert.Equal(snapshot.Replay.Modes.Count, modes.Length);
        Assert.Equal(newLuid, paths[0].SourceInfo.AdapterId.ToUInt64());
        // Unmapped LUIDs pass through untouched.
        var untouched = snapshot.Replay.Paths.FirstOrDefault(p => p.SourceAdapter != storedLuid);
        if (untouched is not null)
        {
            var idx = snapshot.Replay.Paths.IndexOf(untouched);
            Assert.Equal(untouched.SourceAdapter, paths[idx].SourceInfo.AdapterId.ToUInt64());
        }
    }
}

public class EdidReaderTests
{
    /// <summary>Builds a minimal valid EDID base block for parser tests.</summary>
    private static byte[] BuildEdid(string vendor, ushort product, uint serial, string name)
    {
        var edid = new byte[128];
        edid[0] = 0x00;
        for (var i = 1; i <= 6; i++) edid[i] = 0xFF;
        edid[7] = 0x00;

        var packed = ((vendor[0] - 'A' + 1) << 10) | ((vendor[1] - 'A' + 1) << 5) | (vendor[2] - 'A' + 1);
        edid[8] = (byte)(packed >> 8);
        edid[9] = (byte)(packed & 0xFF);
        edid[10] = (byte)(product & 0xFF);
        edid[11] = (byte)(product >> 8);
        edid[12] = (byte)(serial & 0xFF);
        edid[13] = (byte)((serial >> 8) & 0xFF);
        edid[14] = (byte)((serial >> 16) & 0xFF);
        edid[15] = (byte)(serial >> 24);
        edid[21] = 119; // 1190 mm
        edid[22] = 34;  // 340 mm

        // Descriptor block at 54: display name (tag 0xFC).
        edid[57] = 0xFC;
        var text = name + "\n";
        for (var i = 0; i < text.Length && i < 13; i++)
            edid[59 + i] = (byte)text[i];

        return edid;
    }

    [Fact]
    public void Parse_ExtractsVendorProductSerialAndName()
    {
        var info = EdidReader.Parse(BuildEdid("SAM", 0x7454, 12345, "Odyssey G9"));

        Assert.NotNull(info);
        Assert.Equal("SAM", info!.ManufacturerCode);
        Assert.Equal(0x7454, info.ProductCode);
        Assert.Equal(12345u, info.SerialNumber);
        Assert.Equal("Odyssey G9", info.DisplayName);
        Assert.Equal("SAM7454_12345", info.StableId);
        Assert.Equal(1190, info.PhysicalWidthMm);
    }

    [Fact]
    public void Parse_RejectsGarbage()
    {
        Assert.Null(EdidReader.Parse(new byte[128])); // no header magic
        Assert.Null(EdidReader.Parse(new byte[10]));  // too short
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#SAM7454#5&35454913&0&UID4355#{guid}", @"DISPLAY\SAM7454\5&35454913&0&UID4355")]
    [InlineData(@"DISPLAY#ABC1234#1&2&3#{x}", @"DISPLAY\ABC1234\1&2&3")]
    public void DevicePathToInstanceId_Converts(string devicePath, string expected)
    {
        Assert.Equal(expected, EdidReader.DevicePathToInstanceId(devicePath));
    }
}
