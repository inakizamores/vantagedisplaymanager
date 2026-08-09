using System.IO;
using Vantage.Core.Services;

namespace Vantage.App.Services;

/// <summary>
/// Keeps preset shortcuts honest for the life of the install.
///
/// A .lnk records an absolute path, so anything that moves the executable — a Velopack update
/// changing its layout, a portable copy unzipped somewhere else, a reinstall to a different
/// drive — leaves every preset shortcut pointing at a file that is gone. Velopack solves this
/// for its own Start menu entry by rewriting it on every update; this does the same for ours,
/// from three places:
///
///   • Velopack's after-install / after-update hooks, so a shortcut is fixed at the moment the
///     thing that would have broken it happens, not after the user has clicked a dead one.
///   • Every normal launch, as a safety net for the paths Velopack knows nothing about.
///   • Velopack's before-uninstall hook, so uninstalling takes the shortcuts with it instead of
///     leaving dead entries in the Start menu.
///
/// Shortcuts are repaired in place, never deleted and recreated: replacing the file would drop
/// any Start pin the user had made against it. A shortcut the user deleted themselves stays
/// deleted — this repairs, it does not resurrect.
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
                    if (string.Equals(ShortcutService.TargetOf(path), exe, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ShortcutService.Repair(
                            path,
                            $"{LaunchCommand.ApplySwitch} {profile.Id}",
                            $"Switch displays to the '{profile.Name}' preset"))
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

    /// <summary>Removes every preset shortcut. Used from the before-uninstall hook.</summary>
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
}
