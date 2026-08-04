using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;

namespace Vantage.Core.Services;

/// <summary>
/// Versioned JSON profile store (BLUEPRint P8): UTF-8, no polymorphic type handling,
/// atomic writes, backup before any schema migration.
/// </summary>
public sealed class ProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public ProfileStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vantage", "profiles.json");
    }

    public string FilePath => _filePath;

    public ProfileFileEnvelope Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new ProfileFileEnvelope();

            using var stream = File.OpenRead(_filePath);
            var envelope = JsonSerializer.Deserialize<ProfileFileEnvelope>(stream, JsonOptions)
                ?? new ProfileFileEnvelope();

            if (envelope.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"Profile store schema v{envelope.SchemaVersion} is newer than this build supports (v{CurrentSchemaVersion}). Update Vantage.");

            // Future migrations: back up, then transform envelope stepwise to CurrentSchemaVersion.
            return envelope;
        }
    }

    public void Save(ProfileFileEnvelope envelope)
    {
        lock (_gate)
        {
            envelope.LastUpdated = DateTimeOffset.Now;
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            // Atomic write: serialize to a temp file, then swap it in.
            var tmp = _filePath + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, envelope, JsonOptions);
            }

            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, _filePath + ".bak");
            else
                File.Move(tmp, _filePath);
        }
    }

    public VantageProfile? Find(string idOrName)
    {
        var envelope = Load();
        if (Guid.TryParse(idOrName, out var id))
            return envelope.Profiles.FirstOrDefault(p => p.Id == id);
        return envelope.Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(VantageProfile profile)
    {
        lock (_gate)
        {
            var envelope = Load();
            var idx = envelope.Profiles.FindIndex(p => p.Id == profile.Id);
            profile.UpdatedAt = DateTimeOffset.Now;
            if (idx >= 0)
                envelope.Profiles[idx] = profile;
            else
                envelope.Profiles.Add(profile);
            Save(envelope);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var envelope = Load();
            var removed = envelope.Profiles.RemoveAll(p => p.Id == id) > 0;
            if (removed)
                Save(envelope);
            return removed;
        }
    }

    /// <summary>Builds a profile from a live snapshot.</summary>
    public static VantageProfile FromSnapshot(SystemSnapshot snapshot, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Displays = snapshot.Displays.Select(d => new ProfileDisplay
        {
            Identity = d.Identity,
            Enabled = true,
            Primary = d.IsPrimary,
            PositionX = d.PositionX,
            PositionY = d.PositionY,
            Width = d.Width,
            Height = d.Height,
            RefreshMillihertz = d.RefreshMillihertz,
            Rotation = d.Rotation,
            DpiScalePercent = d.Dpi?.CurrentPercent,
            HdrEnabled = d.Hdr.Supported ? d.Hdr.Enabled : null,
            SdrWhiteLevelNits = d.Hdr is { Enabled: true, SdrWhiteLevelNits: not null } ? d.Hdr.SdrWhiteLevelNits : null,
        }).ToList(),
        Replay = snapshot.Replay,
    };
}
