using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Vantage.App.Services;

/// <summary>
/// Global profile hotkeys via RegisterHotKey on a message-only window — no polling
/// (BLUEPRINT: RegisterHotKey over DirectInput threads), and independent of whether
/// the main window exists, so hotkeys work in tray-only mode.
/// Gestures are stored on profiles as "Ctrl+Alt+D1" (WPF Key enum names).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Action<Guid> _onHotkey;
    private readonly Dictionary<int, Guid> _registrations = [];
    private int _nextId = 1;

    public HotkeyService(Action<Guid> onHotkey)
    {
        _onHotkey = onHotkey;
        var parameters = new HwndSourceParameters("VantageHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Re-registers everything; returns gestures that could not be registered (conflicts).</summary>
    public List<string> RegisterAll(IEnumerable<(Guid ProfileId, string Gesture)> hotkeys)
    {
        foreach (var id in _registrations.Keys)
            UnregisterHotKey(_source.Handle, id);
        _registrations.Clear();

        var failures = new List<string>();
        foreach (var (profileId, gesture) in hotkeys)
        {
            if (!TryParseGesture(gesture, out var modifiers, out var vk))
            {
                failures.Add(gesture);
                continue;
            }

            var id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, vk))
                _registrations[id] = profileId;
            else
                failures.Add(FormatGesture(gesture));
        }
        return failures;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registrations.TryGetValue(wParam.ToInt32(), out var profileId))
        {
            _onHotkey(profileId);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN" or "WINDOWS": modifiers |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }

        if (key == Key.None || modifiers == 0)
            return false; // require at least one modifier — bare keys would swallow typing
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    /// <summary>"Ctrl+Alt+D1" → "Ctrl+Alt+1" for display.</summary>
    public static string FormatGesture(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 2 && parts[i][0] is 'D' or 'd' && char.IsDigit(parts[i][1]))
                parts[i] = parts[i][1].ToString();
            else if (parts[i].StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
                parts[i] = "Num " + parts[i][6..];
        }
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _registrations.Keys)
            UnregisterHotKey(_source.Handle, id);
        _registrations.Clear();
        _source.Dispose();
    }
}
