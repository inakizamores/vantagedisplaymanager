using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;

namespace Vantage.Core.Services;

/// <summary>
/// Versioned JSON profile store (BLUEPRINT P8): UTF-8, no polymorphic type handling,
/// atomic writes, backup before any schema migration.
///
/// The file is shared by several writers — the app's view model, the shortcut reconciler on
/// a background thread, and headless <c>--apply</c> processes — so every read-modify-write
/// runs under a named mutex keyed on the file path. In-process, distinct ProfileStore
/// instances over the same file serialize through that same mutex.
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
    private readonly Mutex _fileMutex;

    public ProfileStore(string? filePath = null)
    {
        if (filePath is null)
        {
            // Default store lives in Documents (survives reinstalls, easy to migrate — P8).
            VantageDataPaths.EnsureCreatedAndMigrated();
            _filePath = VantageDataPaths.ProfilesFile;
        }
        else
        {
            _filePath = filePath;
        }

        // Named per path so test stores over temp files don't contend with the real one.
        var key = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(_filePath.ToUpperInvariant())))[..16];
        _fileMutex = new Mutex(false, $@"Local\VantageProfileStore-{key}");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Set when <see cref="Load"/> had to recover from a corrupt file — a sentence the UI
    /// can surface. Cleared on the next clean load.
    /// </summary>
    public string? LastRecoveryMessage { get; private set; }

    public ProfileFileEnvelope Load()
    {
        using var _ = AcquireFileLock();

        if (!File.Exists(_filePath))
            return new ProfileFileEnvelope();

        try
        {
            var envelope = ReadEnvelope(_filePath);
            LastRecoveryMessage = null;
            return envelope;
        }
        catch (JsonException ex)
        {
            // A truncated or mangled file (power loss, interrupted OneDrive sync) must not
            // make the app unstartable. Preserve the evidence, then fall back to the .bak
            // written by the last successful Save.
            AppLog.Error(nameof(ProfileStore), ex, $"'{_filePath}' is corrupt");
            var quarantined = Quarantine();

            var backup = _filePath + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    var restored = ReadEnvelope(backup);
                    File.Copy(backup, _filePath, overwrite: true);
                    LastRecoveryMessage =
                        "The profile file was damaged and has been restored from its automatic backup. " +
                        "Recent changes may be missing.";
                    AppLog.Write(nameof(ProfileStore), $"Restored from backup; corrupt file kept as '{quarantined}'.");
                    return restored;
                }
                catch (JsonException backupEx)
                {
                    AppLog.Error(nameof(ProfileStore), backupEx, "Backup is corrupt too");
                }
            }

            LastRecoveryMessage =
                "The profile file was damaged and could not be recovered. Starting with an empty list — " +
                $"the damaged file was kept as '{Path.GetFileName(quarantined)}'.";
            return new ProfileFileEnvelope();
        }
    }

    private static ProfileFileEnvelope ReadEnvelope(string path)
    {
        using var stream = File.OpenRead(path);
        var envelope = JsonSerializer.Deserialize<ProfileFileEnvelope>(stream, JsonOptions)
            ?? new ProfileFileEnvelope();

        if (envelope.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Profile store schema v{envelope.SchemaVersion} is newer than this build supports (v{CurrentSchemaVersion}). Update Vantage.");

        foreach (var profile in envelope.Profiles)
            RepairIdentities(profile, $"loading '{profile.Name}'");

        // Future migrations: back up, then transform envelope stepwise to CurrentSchemaVersion.
        return envelope;
    }

    /// <summary>
    /// Gives every display in <paramref name="profile"/> a distinct stable id, repairing files
    /// written before identities were disambiguated (issue #9: three identical panels all saved
    /// as <c>AUS43E1_125727</c>). The stored PnP instance path is what tells them apart, and it
    /// is the same input <see cref="DisplayService"/> resolves live ids from — so a repaired
    /// profile lines up with the hardware it was captured on, with no reconfiguration needed.
    ///
    /// Deliberately not gated on <see cref="ProfileFileEnvelope.SchemaVersion"/>: the file's
    /// shape is unchanged, only the id values, so a repaired file still loads in older builds
    /// rather than tripping their "newer than this build supports" guard. The pass is
    /// idempotent and a no-op for the vast majority of profiles, which have no duplicates.
    /// </summary>
    internal static bool RepairIdentities(VantageProfile profile, string context)
    {
        var seeds = profile.Displays
            .Select(d => new MonitorIdentitySeed(d.Identity.StableId, d.Identity.DeviceInstanceId))
            .ToArray();
        if (seeds.Length < 2)
            return false;

        var resolved = MonitorIdentityResolver.Resolve(seeds);
        var changed = false;

        for (var i = 0; i < profile.Displays.Count; i++)
        {
            var display = profile.Displays[i];
            if (string.Equals(resolved[i], display.Identity.StableId, StringComparison.Ordinal))
                continue;

            AppLog.Write(nameof(ProfileStore),
                $"{context}: display '{display.Identity.StableId}' shares its EDID identity; " +
                $"re-keyed as '{resolved[i]}'.");
            profile.Displays[i] = display with { Identity = display.Identity with { StableId = resolved[i] } };
            changed = true;
        }

        return changed;
    }

    /// <summary>Moves the corrupt file aside under a timestamped name and returns that path.</summary>
    private string Quarantine()
    {
        var target = $"{_filePath}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(_filePath, target, overwrite: true);
        }
        catch (IOException)
        {
            // If even the move fails, the JsonException fallback path still writes a fresh file.
        }
        return target;
    }

    public void Save(ProfileFileEnvelope envelope)
    {
        using var _ = AcquireFileLock();

        envelope.LastUpdated = DateTimeOffset.Now;

        // Last line of defense: never persist a profile whose displays can't be told apart,
        // whatever built it.
        foreach (var profile in envelope.Profiles)
            RepairIdentities(profile, $"saving '{profile.Name}'");

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

    /// <summary>
    /// Takes the cross-process mutex for this store file. Reentrant on the same thread, so
    /// Upsert's lock → Load's lock nests fine. An abandoned mutex (a headless process killed
    /// mid-write) is treated as acquired: the atomic temp-file swap means the store itself
    /// is never half-written. A timeout proceeds without the lock rather than hanging the
    /// UI — the worst case is last-writer-wins, never corruption.
    /// </summary>
    private FileLockScope AcquireFileLock()
    {
        var acquired = false;
        try
        {
            acquired = _fileMutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired)
            AppLog.Write(nameof(ProfileStore), "Timed out waiting for the store lock; proceeding unlocked.");
        return new FileLockScope(acquired ? _fileMutex : null);
    }

    private readonly struct FileLockScope(Mutex? held) : IDisposable
    {
        public void Dispose() => held?.ReleaseMutex();
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
        // Held across the read-modify-write so another process can't slip a save in between.
        using var _ = AcquireFileLock();
        var envelope = Load();
        var idx = envelope.Profiles.FindIndex(p => p.Id == profile.Id);
        profile.UpdatedAt = DateTimeOffset.Now;
        if (idx >= 0)
            envelope.Profiles[idx] = profile;
        else
            envelope.Profiles.Add(profile);
        Save(envelope);
    }

    public bool Delete(Guid id)
    {
        using var _ = AcquireFileLock();
        var envelope = Load();
        var removed = envelope.Profiles.RemoveAll(p => p.Id == id) > 0;
        if (removed)
            Save(envelope);
        return removed;
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
            ColorDepthBpc = d.OutputBpc,
        }).ToList(),
        Replay = snapshot.Replay,
    };
}
