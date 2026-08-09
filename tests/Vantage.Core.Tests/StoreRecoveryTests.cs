using System.IO;
using Vantage.Core.Services;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// The store's self-repair paths: a damaged profiles.json (power loss, interrupted OneDrive
/// sync) must never make the app unstartable, and concurrent writers must never lose a
/// profile. These paths only run when something has already gone wrong, which is exactly why
/// they need tests — nobody exercises them by hand.
/// </summary>
public class StoreRecoveryTests
{
    private static Vantage.Core.Models.ReplayPayload EmptyReplay() => new()
    {
        Paths = [],
        Modes = [],
        AdapterPaths = [],
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vantage-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CorruptFile_WithValidBackup_RestoresFromBackup()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            var store = new ProfileStore(path);

            // Two saves so a .bak exists alongside the live file.
            var envelope = new Vantage.Core.Models.ProfileFileEnvelope();
            store.Save(envelope);
            envelope.Profiles.Add(new Vantage.Core.Models.VantageProfile
            {
                Id = Guid.NewGuid(),
                Name = "Survivor",
                Displays = [],
                Replay = EmptyReplay(),
            });
            store.Save(envelope);
            store.Save(envelope); // .bak now also carries "Survivor"

            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"profiles\": [ TRUNCATED");

            var recovered = store.Load();

            Assert.Single(recovered.Profiles);
            Assert.Equal("Survivor", recovered.Profiles[0].Name);
            Assert.NotNull(store.LastRecoveryMessage);
            Assert.Contains("backup", store.LastRecoveryMessage, StringComparison.OrdinalIgnoreCase);
            // The evidence is preserved, not destroyed.
            Assert.NotEmpty(Directory.GetFiles(dir, "profiles.json.corrupt-*"));
            // And the next clean load stops reporting a recovery.
            Assert.NotNull(store.Load());
            Assert.Null(store.LastRecoveryMessage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CorruptFile_NoBackup_StartsEmptyAndQuarantines()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            File.WriteAllText(path, "not json at all");

            var store = new ProfileStore(path);
            var recovered = store.Load();

            Assert.Empty(recovered.Profiles);
            Assert.NotNull(store.LastRecoveryMessage);
            Assert.NotEmpty(Directory.GetFiles(dir, "profiles.json.corrupt-*"));
            Assert.False(File.Exists(path), "the corrupt file should have been moved aside");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentUpserts_FromSeparateStoreInstances_LoseNothing()
    {
        // The app runs at least three writers over one file: the view model, the shortcut
        // reconciler on a background thread, and headless --apply processes. Distinct
        // ProfileStore instances model that; the named mutex must serialize them.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            const int writers = 4, perWriter = 10;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                var store = new ProfileStore(path);
                for (var i = 0; i < perWriter; i++)
                    store.Upsert(new Vantage.Core.Models.VantageProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = $"W{w}-{i}",
                        Displays = [],
                        Replay = EmptyReplay(),
                    });
            })).ToArray();
            await Task.WhenAll(tasks);

            Assert.Equal(writers * perWriter, new ProfileStore(path).Load().Profiles.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class DataMigrationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vantage-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Migration_CopiesLegacyFiles_Once()
    {
        var work = TempDir();
        try
        {
            var root = Path.Combine(work, "docs");
            var legacy = Path.Combine(work, "legacy");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "profiles.json"), "{\"schemaVersion\":1}");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

            VantageDataPaths.EnsureCreatedAndMigrated(root, legacy);

            Assert.True(File.Exists(Path.Combine(root, "profiles.json")));
            Assert.True(File.Exists(Path.Combine(root, "settings.json")));
            // Read-only migration: the legacy source is left in place.
            Assert.True(File.Exists(Path.Combine(legacy, "profiles.json")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Migration_NeverOverwrites_ExistingData()
    {
        var work = TempDir();
        try
        {
            var root = Path.Combine(work, "docs");
            var legacy = Path.Combine(work, "legacy");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(root, "profiles.json"), "CURRENT");
            File.WriteAllText(Path.Combine(legacy, "profiles.json"), "STALE");

            VantageDataPaths.EnsureCreatedAndMigrated(root, legacy);

            Assert.Equal("CURRENT", File.ReadAllText(Path.Combine(root, "profiles.json")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Migration_NoLegacyFolder_JustCreatesRoot()
    {
        var work = TempDir();
        try
        {
            var root = Path.Combine(work, "docs");
            VantageDataPaths.EnsureCreatedAndMigrated(root, Path.Combine(work, "nope"));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
