using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;
using Vantage.Core.Services;

namespace Vantage.Cli;

/// <summary>
/// Headless CLI twin of the Vantage GUI (BLUEPRINT P10).
/// Exit codes: 0 success, 1 usage error, 2 not found, 3 apply failed/unverified.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static async Task<int> Main(string[] args)
    {
        var json = args.Contains("--json");
        args = args.Where(a => a != "--json").ToArray();

        if (args.Length == 0)
            return Usage();

        var displayService = new DisplayService();
        var store = new ProfileStore();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return List(displayService, json);
                case "profiles":
                    return Profiles(displayService, store, json);
                case "capture":
                    return Capture(displayService, store, args.Skip(1).ToArray());
                case "apply":
                    return await Apply(displayService, store, args.Skip(1).ToArray());
                case "active":
                    return Active(displayService, store, json);
                case "delete":
                    return Delete(store, args.Skip(1).ToArray());
                case "hdr":
                    return await Hdr(displayService, args.Skip(1).ToArray());
                case "modes":
                    return Modes(displayService, args.Skip(1).ToArray());
                case "snapshot":
                    // Full SystemSnapshot dump — used to record test fixtures and for diagnostics.
                    Console.WriteLine(JsonSerializer.Serialize(displayService.Capture(), JsonOut));
                    return 0;
                case "variant":
                    return Variant(displayService, store, args.Skip(1).ToArray());
                default:
                    return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 3;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Vantage Display Manager CLI

            usage:
              vantage list [--json]              Show connected displays and their state
              vantage profiles [--json]          Show saved profiles and whether each is active
              vantage capture <name>             Save the current configuration as a profile
              vantage apply <name-or-id>         Apply a saved profile (verified)
              vantage active [--json]            Show which profile matches the current state
              vantage delete <name-or-id>        Delete a profile
              vantage hdr <on|off> [display#]    Toggle HDR (all HDR-capable displays, or one)
              vantage modes [display#]           List supported resolutions/refresh rates
              vantage variant <name> --display <n> [--res WxH] [--hz N] [--hdr on|off]
                                                 Create a profile from the current setup with
                                                 a different mode/HDR on one display
            """);
        return 1;
    }

    private static int List(DisplayService displayService, bool json)
    {
        var snapshot = displayService.Capture();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot.Displays, JsonOut));
            return 0;
        }

        Console.WriteLine($"{snapshot.Displays.Count} active display(s):");
        Console.WriteLine();
        var i = 0;
        foreach (var d in snapshot.Displays)
        {
            Console.WriteLine($"  [{i++}] {d.Identity.FriendlyName ?? "(unnamed)"}  {(d.IsPrimary ? "· PRIMARY" : "")}");
            Console.WriteLine($"      Identity     {d.Identity.StableId}");
            Console.WriteLine($"      Mode         {d.Width}x{d.Height} @ {d.RefreshHz:0.###} Hz, {d.Rotation}, pos ({d.PositionX},{d.PositionY})");
            Console.WriteLine($"      Connection   {d.OutputTechnology} via {d.GdiDeviceName}");
            var hdrDesc = d.Hdr.Supported
                ? $"{(d.Hdr.Enabled ? "ON" : "off")} (mode {d.Hdr.ActiveColorMode ?? "n/a"}, {d.Hdr.BitsPerColorChannel}-bit {d.Hdr.ColorEncoding}{(d.Hdr.SdrWhiteLevelNits is { } n ? $", SDR white {n:0} nits" : "")})"
                : "not supported";
            Console.WriteLine($"      HDR          {hdrDesc}");
            if (d.OutputBpc is { } bpc)
                Console.WriteLine($"      Output       {bpc} bpc (GPU)");
            if (d.Dpi is { } dpi)
                Console.WriteLine($"      Scale        {dpi.CurrentPercent}% (recommended {dpi.RecommendedPercent}%)");
            if (d.PhysicalWidthMm > 0)
                Console.WriteLine($"      Physical     {d.PhysicalWidthMm}x{d.PhysicalHeightMm} mm");
            Console.WriteLine();
        }
        return 0;
    }

    private static int Profiles(DisplayService displayService, ProfileStore store, bool json)
    {
        var envelope = store.Load();
        var snapshot = displayService.Capture();
        if (json)
        {
            var rows = envelope.Profiles.Select(p =>
            {
                var m = ProfileMatcher.Match(p, snapshot);
                return new { p.Id, p.Name, active = m.IsActive, possible = m.IsPossible, displays = p.Displays.Count };
            });
            Console.WriteLine(JsonSerializer.Serialize(rows, JsonOut));
            return 0;
        }

        if (envelope.Profiles.Count == 0)
        {
            Console.WriteLine("No profiles saved. Use: vantage capture <name>");
            return 0;
        }

        foreach (var p in envelope.Profiles)
        {
            var m = ProfileMatcher.Match(p, snapshot);
            var status = m.IsActive ? "ACTIVE" : m.IsPossible ? "available" : "displays missing";
            Console.WriteLine($"  {p.Name,-30} {status,-18} {p.Displays.Count} display(s)  {p.Id}");
        }
        return 0;
    }

    private static int Capture(DisplayService displayService, ProfileStore store, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vantage capture <name>");
            return 1;
        }

        var name = string.Join(' ', args);
        var existing = store.Find(name);
        var snapshot = displayService.Capture();
        var profile = ProfileStore.FromSnapshot(snapshot, name);
        if (existing is not null)
        {
            profile = profile with { Id = existing.Id, CreatedAt = existing.CreatedAt };
            Console.WriteLine($"Updating existing profile '{name}'.");
        }
        store.Upsert(profile);
        Console.WriteLine($"Saved profile '{name}' with {profile.Displays.Count} display(s) -> {store.FilePath}");
        return 0;
    }

    private static async Task<int> Apply(DisplayService displayService, ProfileStore store, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vantage apply <name-or-id>");
            return 1;
        }

        var profile = store.Find(string.Join(' ', args));
        if (profile is null)
        {
            Console.Error.WriteLine("Profile not found.");
            return 2;
        }

        var engine = new ApplyEngine(displayService);
        var progress = new Progress<ApplyProgress>(p => Console.WriteLine($"  {p.Step}: {p.Message}"));
        var report = await engine.ApplyAsync(profile, progress);

        if (report.Succeeded)
        {
            Console.WriteLine($"Profile '{profile.Name}' applied and verified.");
            return 0;
        }

        Console.Error.WriteLine($"Apply finished with problems: {report.FailureReason}");
        if (report.FinalMatch is { } match)
        {
            foreach (var d in match.Displays.Where(d => d.Kind == DisplayMatchKind.Mismatch))
                foreach (var diff in d.Diffs)
                    Console.Error.WriteLine($"    {d.ProfileIdentity.FriendlyName}: {diff.Field} expected {diff.Expected}, got {diff.Actual}");
        }
        return 3;
    }

    private static int Active(DisplayService displayService, ProfileStore store, bool json)
    {
        var snapshot = displayService.Capture();
        var envelope = store.Load();
        var active = envelope.Profiles.FirstOrDefault(p => ProfileMatcher.Match(p, snapshot).IsActive);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { active = active?.Name, id = active?.Id }, JsonOut));
            return 0;
        }
        Console.WriteLine(active is null ? "No saved profile matches the current configuration." : $"Active profile: {active.Name}");
        return active is null ? 2 : 0;
    }

    private static int Delete(ProfileStore store, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vantage delete <name-or-id>");
            return 1;
        }
        var profile = store.Find(string.Join(' ', args));
        if (profile is null)
        {
            Console.Error.WriteLine("Profile not found.");
            return 2;
        }
        store.Delete(profile.Id);
        Console.WriteLine($"Deleted '{profile.Name}'.");
        return 0;
    }

    private static int Modes(DisplayService displayService, string[] args)
    {
        var snapshot = displayService.Capture();
        int? index = args.Length > 0 && int.TryParse(args[0], out var i) ? i : null;

        for (var n = 0; n < snapshot.Displays.Count; n++)
        {
            if (index is not null && n != index)
                continue;
            var d = snapshot.Displays[n];
            Console.WriteLine($"[{n}] {d.Identity.FriendlyName} ({d.GdiDeviceName})");
            if (d.GdiDeviceName is not { Length: > 0 } gdiName)
                continue;
            foreach (var group in Vantage.Interop.Gdi.GdiApi.EnumerateModes(gdiName)
                         .GroupBy(m => (m.Width, m.Height)))
            {
                var rates = string.Join(", ", group.Select(m => m.RefreshHz).Distinct().OrderDescending());
                Console.WriteLine($"    {group.Key.Width,5} x {group.Key.Height,-5}  @ {rates} Hz");
            }
            Console.WriteLine();
        }
        return 0;
    }

    private static int Variant(DisplayService displayService, ProfileStore store, string[] args)
    {
        string? name = null;
        int? displayIndex = null;
        uint? width = null, height = null, hz = null;
        bool? hdr = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--display" when i + 1 < args.Length && int.TryParse(args[++i], out var d):
                    displayIndex = d;
                    break;
                case "--res" when i + 1 < args.Length:
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length == 2 && uint.TryParse(parts[0], out var w) && uint.TryParse(parts[1], out var h))
                    {
                        width = w;
                        height = h;
                    }
                    break;
                case "--hz" when i + 1 < args.Length && uint.TryParse(args[++i], out var f):
                    hz = f;
                    break;
                case "--hdr" when i + 1 < args.Length:
                    hdr = args[++i] == "on";
                    break;
                default:
                    name = name is null ? args[i] : $"{name} {args[i]}";
                    break;
            }
        }

        if (name is null || displayIndex is null || (width is null && height is null && hz is null && hdr is null))
        {
            Console.Error.WriteLine("usage: vantage variant <name> --display <n> [--res WxH] [--hz N] [--hdr on|off]");
            return 1;
        }

        var snapshot = displayService.Capture();
        if (displayIndex < 0 || displayIndex >= snapshot.Displays.Count)
        {
            Console.Error.WriteLine($"Display #{displayIndex} does not exist (see: vantage list).");
            return 2;
        }

        var target = snapshot.Displays[displayIndex.Value];
        var profile = ProfileVariantBuilder.Build(snapshot, name,
        [
            new DisplayOverride
            {
                StableId = target.Identity.StableId,
                Width = width,
                Height = height,
                RefreshHz = hz,
                HdrEnabled = hdr,
            },
        ]);

        var existing = store.Find(name);
        if (existing is not null)
        {
            profile = profile with { Id = existing.Id, CreatedAt = existing.CreatedAt };
            Console.WriteLine($"Updating existing profile '{name}'.");
        }
        store.Upsert(profile);

        var d0 = profile.Displays.First(p => p.Identity.StableId == target.Identity.StableId);
        Console.WriteLine($"Saved variant '{name}': {target.Identity.FriendlyName} -> {d0.Width}x{d0.Height} @ {Math.Round(d0.RefreshMillihertz / 1000.0)} Hz" +
                          (d0.HdrEnabled is { } hv ? $", HDR {(hv ? "on" : "off")}" : ""));
        return 0;
    }

    private static async Task<int> Hdr(DisplayService displayService, string[] args)
    {
        if (args.Length == 0 || args[0] is not ("on" or "off"))
        {
            Console.Error.WriteLine("usage: vantage hdr <on|off> [display#]");
            return 1;
        }
        var enable = args[0] == "on";
        int? index = args.Length > 1 && int.TryParse(args[1], out var i) ? i : null;

        var snapshot = displayService.Capture();
        var targets = snapshot.Displays
            .Where((d, n) => d.Hdr.Supported && (index is null || n == index))
            .ToList();

        if (targets.Count == 0)
        {
            Console.Error.WriteLine(index is null ? "No HDR-capable displays found." : "That display is not HDR-capable or does not exist.");
            return 2;
        }

        // Reuse the engine's verified HDR path by building a minimal single-purpose profile.
        var engine = new ApplyEngine(displayService);
        var pseudo = new VantageProfile
        {
            Id = Guid.NewGuid(),
            Name = "(hdr toggle)",
            Displays = snapshot.Displays.Select(d => new ProfileDisplay
            {
                Identity = d.Identity,
                Primary = d.IsPrimary,
                PositionX = d.PositionX,
                PositionY = d.PositionY,
                Width = d.Width,
                Height = d.Height,
                RefreshMillihertz = d.RefreshMillihertz,
                Rotation = d.Rotation,
                HdrEnabled = targets.Any(t => t.Identity.StableId == d.Identity.StableId) ? enable : null,
            }).ToList(),
            Replay = snapshot.Replay,
        };

        var report = await engine.ApplyAsync(pseudo);
        foreach (var line in report.Log.Where(l => l.Contains("ApplyHdr")))
            Console.WriteLine($"  {line}");
        Console.WriteLine(report.Succeeded ? "Done." : "Completed with warnings — check output above.");
        return report.Succeeded ? 0 : 3;
    }
}
