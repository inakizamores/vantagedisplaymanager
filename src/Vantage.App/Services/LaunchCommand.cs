namespace Vantage.App.Services;

/// <summary>
/// What a launch of Vantage.exe is asking for. The same shape travels two ways: parsed from
/// the command line on a cold start, and serialised over <see cref="InstanceChannel"/> when
/// an instance is already running and should do the work instead.
/// </summary>
public sealed record LaunchCommand(string? ApplyTarget, bool TrayOnly, bool Update = false, bool Help = false)
{
    /// <summary>Switch a preset shortcut carries: <c>--apply &lt;profile id or name&gt;</c>.</summary>
    public const string ApplySwitch = "--apply";

    /// <summary>Headless update: check, download, install, relaunch. Scriptable, and how the
    /// update path gets verified without driving the window.</summary>
    public const string UpdateSwitch = "--update";

    public static LaunchCommand Parse(IReadOnlyList<string> args)
    {
        string? applyTarget = null;
        var trayOnly = false;
        var update = args.Any(a => string.Equals(a, UpdateSwitch, StringComparison.OrdinalIgnoreCase));
        var help = args.Any(a => a is "--help" or "-h" or "/?" or "-?");

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, StartupManager.TrayArgument, StringComparison.OrdinalIgnoreCase))
            {
                trayOnly = true;
            }
            else if (arg.StartsWith(ApplySwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                applyTarget = arg[(ApplySwitch.Length + 1)..].Trim('"');
            }
            else if (string.Equals(arg, ApplySwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                // Names can contain spaces, and a .lnk hands them to us already split —
                // so take everything up to the next switch, not just the next word.
                applyTarget = string.Join(' ', args
                    .Skip(i + 1)
                    .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))).Trim('"');
                break;
            }
        }

        return new LaunchCommand(applyTarget is { Length: > 0 } ? applyTarget : null, trayOnly, update, help);
    }

    /// <summary>Line protocol for the IPC channel — one verb, optional argument.</summary>
    public string ToMessage() => ApplyTarget is { } target ? $"apply {target}" : "show";

    public static LaunchCommand FromMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.StartsWith("apply ", StringComparison.Ordinal)
            ? new LaunchCommand(trimmed["apply ".Length..].Trim(), TrayOnly: false)
            : new LaunchCommand(null, TrayOnly: false);
    }
}
