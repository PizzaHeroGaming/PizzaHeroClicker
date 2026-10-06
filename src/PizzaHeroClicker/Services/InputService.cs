using System.Runtime.InteropServices;
using PizzaHeroClicker.Models;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Synthesises mouse and keyboard input with SendInput. Coordinates are physical
/// virtual-screen pixels (negative values are valid on monitors left of / above the primary).
/// Safe to call from any thread.
/// </summary>
public sealed class InputService : IInputService
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private readonly object _lock = new();
    private readonly HashSet<ClickButton> _heldButtons = new();
    private readonly List<int> _heldKeys = new();
    private long _lastFailureLogTicks;

    public (int X, int Y) GetCursor()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public void MoveTo(int x, int y)
    {
        // SendInput takes absolute positions normalised to 0..65535 across the virtual desktop.
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;

        x = Math.Clamp(x, vx, vx + vw - 1);
        y = Math.Clamp(y, vy, vy + vh - 1);

        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            NormalizeAbsolute(x - vx, vw), NormalizeAbsolute(y - vy, vh)));
    }

    /// <summary>
    /// Converts a pixel offset within a surface of <paramref name="size"/> pixels to the
    /// 0..65535 scale. Windows converts back with a truncating division, so a naive
    /// "offset * 65535 / size" lands one pixel short on many coordinates. Rounding UP to the
    /// first normalised value that maps to the pixel is exact for every pixel.
    /// </summary>
    public static int NormalizeAbsolute(int offset, int size) =>
        (int)(((long)offset * 65536 + size - 1) / size);

    public void Click(ClickButton button)
    {
        var (down, up) = Flags(button);
        Send(Mouse(down), Mouse(up));
    }

    public void ButtonDown(ClickButton button)
    {
        lock (_lock) _heldButtons.Add(button);
        Send(Mouse(Flags(button).Down));
    }

    public void ButtonUp(ClickButton button)
    {
        lock (_lock) _heldButtons.Remove(button);
        Send(Mouse(Flags(button).Up));
    }

    public void Scroll(ScrollDirection direction, int notches)
    {
        if (notches <= 0) return;
        bool horizontal = direction is ScrollDirection.Left or ScrollDirection.Right;
        // Positive = away from the user (up) or to the right.
        int sign = direction is ScrollDirection.Up or ScrollDirection.Right ? 1 : -1;
        var input = Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);
        input.U.mi.mouseData = unchecked((uint)(sign * notches * WHEEL_DELTA));
        Send(input);
    }

    public void KeyComboDown(KeyCombo combo)
    {
        if (combo.IsEmpty) return;
        foreach (int mod in ModifierKeys(combo.Mods)) KeyDown(mod);
        KeyDown(combo.Vk);
    }

    public void KeyComboUp(KeyCombo combo)
    {
        if (combo.IsEmpty) return;
        KeyUp(combo.Vk);
        foreach (int mod in ModifierKeys(combo.Mods).Reverse()) KeyUp(mod);
    }

    public void ReleaseAll()
    {
        ClickButton[] buttons;
        int[] keys;
        lock (_lock)
        {
            buttons = _heldButtons.ToArray();
            keys = _heldKeys.ToArray();
        }
        foreach (var b in buttons) ButtonUp(b);
        foreach (int k in keys.Reverse()) KeyUp(k);
    }

    private void KeyDown(int vk)
    {
        lock (_lock) _heldKeys.Add(vk);
        Send(Keyboard(vk, up: false));
    }

    private void KeyUp(int vk)
    {
        lock (_lock) _heldKeys.Remove(vk);
        Send(Keyboard(vk, up: true));
    }

    private static IEnumerable<int> ModifierKeys(KeyMods mods)
    {
        if (mods.HasFlag(KeyMods.Ctrl)) yield return 0x11;  // VK_CONTROL
        if (mods.HasFlag(KeyMods.Alt)) yield return 0x12;   // VK_MENU
        if (mods.HasFlag(KeyMods.Shift)) yield return 0x10; // VK_SHIFT
        if (mods.HasFlag(KeyMods.Win)) yield return 0x5B;   // VK_LWIN
    }

    private static (uint Down, uint Up) Flags(ClickButton button) => button switch
    {
        ClickButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        ClickButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } },
    };

    private static INPUT Keyboard(int vk, bool up)
    {
        // Send the virtual key AND its scan code: ordinary apps read the key, while games
        // that use DirectInput / raw input read the scan code.
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = flags },
            },
        };
    }

    /// <summary>Keys on the "extended" part of the keyboard (arrows, navigation cluster, right-hand modifiers).</summary>
    private static bool IsExtendedKey(int vk) =>
        vk is (>= 0x21 and <= 0x28)   // PageUp, PageDown, End, Home, arrows
            or 0x2C or 0x2D or 0x2E   // PrintScreen, Insert, Delete
            or 0x5B or 0x5C or 0x5D   // LWin, RWin, Apps
            or 0x6F or 0x90           // Numpad divide, NumLock
            or 0xA3 or 0xA5;          // RControl, RAlt

    private void Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent == inputs.Length) return;

        // Usually UIPI: the foreground window belongs to an elevated process. Log at most once a second.
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFailureLogTicks) > 1000)
        {
            Interlocked.Exchange(ref _lastFailureLogTicks, now);
            Log.Warn($"SendInput was blocked (error {Marshal.GetLastWin32Error()}). If the target runs as administrator, run the clicker as administrator too.");
        }
    }
}
