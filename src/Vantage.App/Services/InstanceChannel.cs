using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Vantage.App.Services;

/// <summary>
/// One-line command channel to the instance that already owns the single-instance mutex.
///
/// This is what makes a preset shortcut feel instant when Vantage is sitting in the tray:
/// the launched process connects, writes one line and exits, and the warm instance — which
/// already has the display service and the profile store loaded — does the applying. The
/// second process never touches the display APIs at all.
///
/// The pipe name carries the user's SID so two people signed in at once each get their own.
/// </summary>
public static class InstanceChannel
{
    private static string PipeName =>
        $"VantageDisplayManager.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    /// <summary>
    /// Hands <paramref name="message"/> to the running instance. False means nobody answered —
    /// the caller is on its own (the instance is still starting, or died holding the mutex).
    /// </summary>
    public static bool TrySend(string message, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts listening on a background thread. Messages are raised on that thread — the
    /// handler is responsible for getting itself back onto the UI dispatcher.
    /// </summary>
    public static void StartServer(Action<string> onMessage)
    {
        new Thread(() => Listen(onMessage))
        {
            IsBackground = true,
            Name = "Vantage IPC",
        }.Start();
    }

    private static void Listen(Action<string> onMessage)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();

                using var reader = new StreamReader(server);
                if (reader.ReadLine() is { Length: > 0 } line)
                    onMessage(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A client that hung up mid-write, or a transient name collision while the
                // previous server instance is being torn down. Back off, then listen again.
                Thread.Sleep(250);
            }
        }
    }
}
