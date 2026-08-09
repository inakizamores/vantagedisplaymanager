using System.Text;

namespace Vantage.Core.Services;

/// <summary>
/// Minimal rolling diagnostic log, written beside the profiles so a bug report can include
/// it ("send the log from Documents\Vantage Display Manager"). One file, rolled once at
/// 512 KB to keep the pair bounded at ~1 MB. Logging must never take the app down, so every
/// write is best-effort and swallows I/O failures.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long RollAtBytes = 512 * 1024;

    public static string LogFile => Path.Combine(VantageDataPaths.Root, "vantage.log");
    private static string PreviousLogFile => LogFile + ".old";

    public static void Write(string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(VantageDataPaths.Root);
                RollIfNeeded();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFile, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a diagnostic aid, never a dependency.
        }
    }

    public static void Error(string source, Exception exception, string? context = null) =>
        Write(source, context is null ? exception.ToString() : $"{context}{Environment.NewLine}{exception}");

    /// <summary>Writes a multi-line block (e.g. an apply report's step log) as one entry.</summary>
    public static void WriteBlock(string source, string header, IEnumerable<string> lines) =>
        Write(source, header + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines));

    private static void RollIfNeeded()
    {
        var info = new FileInfo(LogFile);
        if (!info.Exists || info.Length < RollAtBytes)
            return;
        File.Delete(PreviousLogFile);
        File.Move(LogFile, PreviousLogFile);
    }
}
