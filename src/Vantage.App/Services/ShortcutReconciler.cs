using System.IO;
using Vantage.Core.Models;
using Vantage.Core.Services;

namespace Vantage.App.Services;

/// <summary>What <see cref="ShortcutReconciler.Restore"/> did, for the Settings page to report.</summary>
public sealed record ShortcutRestoreResult(int Recreated, int Repaired, int Intact)
{
    public int Total => Recreated + Repaired + Intact;
}

/// <summary>
/// Keeps preset shortcuts honest for the life of the install.
///
/// A .lnk records an absolute path, so anything that moves the executable — a Velopack update
/// changing its layout, a portable copy unzipped somewhere else, a reinstall to a different
/// drive — leaves every preset shortcut pointing at a file that is gone. Worse, updating by
/// running the full Setup.exe over an existing install uninstalls the old version first, and
/// the old version's before-uninstall hook deletes every preset shortcut on the way out. Two
/// answers, for the two situations:
///
///   • <see cref="Restore"/> — after-install / after-update hooks, and the Settings button.
///     Recreates any recorded shortcut whose file is gone, at the exact path it used to
///     occupy: a Start pin made against a deleted .lnk comes back to life when a file
///     reappears at the pinned path. Shortcuts that survived are repaired in place.
///   • <see cref="Reconcile"/> — every normal launch. Repairs in place and forgets shortcuts
///     the user deleted from Explorer; deliberately never resurrects, so a deletion made
///     outside the app stays made.
///
/// Live shortcuts are always repaired in place, never deleted and recreated: replacing an
/// existing file would drop any Start pin the user had made against it.
/// </summary>
public static class ShortcutReconciler
{
    /// <summary>
    /// Re-points every recorded shortcut at the running executable and forgets the ones that no
    /// longer exist. Cheap when nothing has moved: one string compare per shortcut, and the COM
    /// work only happens for files that are actually stale.
    /// </summary>
    public static void Reconcile(ProfileStore store)
    {
        if (Environment.ProcessPath is not { } exe)
            return;

        try
        {
            var envelope = store.Load();
            var changed = false;

            foreach (var profile in envelope.Profiles)
            {
                if (profile.ShortcutPaths is not { Count: > 0 } recorded)
                    continue;

                var live = recorded.Where(File.Exists).ToList();
                if (live.Count != recorded.Count)
                {
                    // The user deleted it from Explorer — stop claiming the profile has one.
                    profile.ShortcutPaths = live.Count > 0 ? live : null;
                    changed = true;
                }

                foreach (var path in live)
                {
                    if (!NeedsRepair(path, exe, out var iconDead))
                        continue;

                    // allowRender: false — this runs on a background pool thread, where WPF
                    // rendering is off limits. An icon already on disk is still found.
                    if (ShortcutService.Repair(
                            path,
                            ArgumentsFor(profile),
                            DescriptionFor(profile),
                            iconDead ? ResolveIcon(profile, allowRender: false) : null))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
                store.Save(envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Never let shortcut housekeeping stop the app from starting.
        }
    }

    /// <summary>
    /// Puts every recorded shortcut back the way the user made it: missing files are recreated
    /// at their recorded paths, survivors are repaired if stale. Called from Velopack's
    /// after-install and after-update hooks — the moments right after something may have
    /// deleted or orphaned them — and from the Settings page as the manual escape hatch.
    /// </summary>
    public static ShortcutRestoreResult Restore(ProfileStore store)
    {
        int recreated = 0, repaired = 0, intact = 0;

        if (Environment.ProcessPath is not { } exe)
            return new ShortcutRestoreResult(0, 0, 0);

        try
        {
            foreach (var profile in store.Load().Profiles)
            {
                // Resolved at most once per profile — rendering a replacement icon is the
                // expensive part, and both of a profile's shortcuts wear the same one.
                string? icon = null;
                var iconResolved = false;
                string? Icon()
                {
                    if (!iconResolved)
                    {
                        icon = ResolveIcon(profile, allowRender: true);
                        iconResolved = true;
                    }
                    return icon;
                }

                foreach (var path in profile.ShortcutPaths ?? [])
                {
                    try
                    {
                        if (!File.Exists(path))
                        {
                            ShortcutService.CreateAt(path, ArgumentsFor(profile), DescriptionFor(profile), Icon());
                            recreated++;
                        }
                        else if (NeedsRepair(path, exe, out var iconDead))
                        {
                            if (ShortcutService.Repair(
                                    path,
                                    ArgumentsFor(profile),
                                    DescriptionFor(profile),
                                    iconDead ? Icon() : null))
                            {
                                repaired++;
                            }
                        }
                        else
                        {
                            intact++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // One unwritable shortcut must not stop the rest from being restored.
                        AppLog.Error(nameof(ShortcutReconciler), ex, $"Could not restore shortcut '{path}'");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error(nameof(ShortcutReconciler), ex, "Shortcut restore failed");
        }

        return new ShortcutRestoreResult(recreated, repaired, intact);
    }

    /// <summary>
    /// Removes every preset shortcut. Used from the before-uninstall hook. The paths stay
    /// recorded in the profile store on purpose: the store lives in Documents and survives
    /// the uninstall, so a later install — including the uninstall-then-install a full
    /// Setup.exe performs over an existing copy — can put every shortcut back via
    /// <see cref="Restore"/>.
    /// </summary>
    public static void RemoveAll(ProfileStore store)
    {
        try
        {
            foreach (var path in store.Load().Profiles.SelectMany(p => p.ShortcutPaths ?? []))
                ShortcutService.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An uninstall must not fail because a shortcut was already gone.
        }
    }

    private static string ArgumentsFor(VantageProfile profile) => $"{LaunchCommand.ApplySwitch} {profile.Id}";

    private static string DescriptionFor(VantageProfile profile) => $"Switch displays to the '{profile.Name}' preset";

    /// <summary>True when the shortcut points at a different exe, or its icon file is gone.</summary>
    private static bool NeedsRepair(string path, string exe, out bool iconDead)
    {
        var icon = ShortcutService.IconOf(path);
        iconDead = icon is null || !File.Exists(Environment.ExpandEnvironmentVariables(icon));

        return iconDead
            || !string.Equals(ShortcutService.TargetOf(path), exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The icon a recreated shortcut should wear, best effort. Prefers what already exists on
    /// disk — the user's own .ico/.exe pick, then the profile's generated icon in Documents,
    /// which survives updates and reinstalls. Only when nothing survived is a fresh icon
    /// rendered, and only where WPF rendering is allowed (an STA thread). Null falls back to
    /// the exe's own icon.
    /// </summary>
    private static string? ResolveIcon(VantageProfile profile, bool allowRender)
    {
        try
        {
            if (profile.IconPath is { Length: > 0 } custom && File.Exists(custom) &&
                ShortcutIcon.IsIconContainer(custom))
            {
                return custom;
            }

            if (Directory.Exists(VantageDataPaths.ShortcutIconsDir))
            {
                var generated = Directory
                    .EnumerateFiles(VantageDataPaths.ShortcutIconsDir, profile.Id + "*.ico")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (generated is not null)
                    return generated;
            }

            if (!allowRender)
                return null;

            return profile.IconPath is { Length: > 0 } image && File.Exists(image)
                ? ShortcutIcon.WriteImageIcon(profile.Id, image)
                : ShortcutIcon.WriteLayoutIcon(profile.Id, ThumbnailDisplay.From(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
