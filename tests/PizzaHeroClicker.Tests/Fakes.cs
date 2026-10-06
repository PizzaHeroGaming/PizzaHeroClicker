using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Tests;

/// <summary>Records what the engine asked for instead of touching the real mouse and keyboard.</summary>
internal sealed class FakeInput : IInputService
{
    private readonly object _lock = new();
    public List<(long Ticks, string Event)> Events { get; } = new();
    public (int X, int Y) Cursor = (500, 500);
    public int HeldCount;

    private void Add(string e)
    {
        lock (_lock) Events.Add((TimingEngine.Now, e));
    }

    public List<(long Ticks, string Event)> Snapshot()
    {
        lock (_lock) return Events.ToList();
    }

    public (int X, int Y) GetCursor() => Cursor;
    public void MoveTo(int x, int y) { Cursor = (x, y); Add($"move {x},{y}"); }
    /// <summary>Called on the engine thread for every click, with the cursor position at that moment.</summary>
    public Action<ClickButton, int, int>? Clicked;

    public void Click(ClickButton button)
    {
        Add($"click {button}");
        Clicked?.Invoke(button, Cursor.X, Cursor.Y);
    }
    public void ButtonDown(ClickButton button) { HeldCount++; Add($"down {button}"); }
    public void ButtonUp(ClickButton button) { HeldCount--; Add($"up {button}"); }
    public void Scroll(ScrollDirection direction, int notches) => Add($"scroll {direction} {notches}");
    public void KeyComboDown(KeyCombo combo) { HeldCount++; Add($"keydown {combo}"); }
    public void KeyComboUp(KeyCombo combo) { HeldCount--; Add($"keyup {combo}"); }
    public void ReleaseAll() { HeldCount = 0; Add("releaseAll"); }
}

internal sealed class FakeScreen : IScreenService
{
    public Func<int, int, PixelColor?> Pixel = (_, _) => new PixelColor(0, 0, 0);
    public Func<int, int, bool> Corner = (_, _) => false;

    public PixelColor? GetPixel(int x, int y) => Pixel(x, y);
    public bool IsAtStuckCorner(int x, int y) => Corner(x, y);

    /// <summary>Returns the pixels for a capture request, or null to simulate a failed capture.</summary>
    public Func<int, int, int, int, int[]?> Capture = (_, _, _, _) => null;

    public bool TryCapture(int x, int y, int width, int height, ref int[] pixels)
    {
        if (Capture(x, y, width, height) is not { } captured) return false;
        pixels = captured;
        return true;
    }
}

internal sealed class FakeWindows : IWindowService
{
    public IntPtr Handle = new(1234);
    public volatile bool Foreground = true;
    public (int X, int Y) Origin = (0, 0);

    public IntPtr Find(WindowTarget target) => Handle;
    public bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && hwnd == Handle;
    public bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && Foreground;
    public (int X, int Y) GetClientOrigin(IntPtr hwnd) => Origin;

    public volatile bool OwnWindowEverywhere;
    public bool IsOwnWindowAt(int x, int y) => OwnWindowEverywhere;
}
