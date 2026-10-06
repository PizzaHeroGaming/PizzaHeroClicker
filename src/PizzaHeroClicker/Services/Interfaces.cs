using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

// The click engine talks to the outside world only through these three interfaces, so it
// can be driven by fakes in tests without moving the real mouse.

public interface IInputService
{
    (int X, int Y) GetCursor();
    void MoveTo(int x, int y);
    /// <summary>Press and release in one atomic SendInput call.</summary>
    void Click(ClickButton button);
    void ButtonDown(ClickButton button);
    void ButtonUp(ClickButton button);
    void Scroll(ScrollDirection direction, int notches);
    void KeyComboDown(KeyCombo combo);
    void KeyComboUp(KeyCombo combo);
    /// <summary>Releases every button and key this service is still holding down.</summary>
    void ReleaseAll();
}

public interface IScreenService
{
    /// <summary>Colour of one screen pixel, or null if the point is off-screen.</summary>
    PixelColor? GetPixel(int x, int y);
    /// <summary>True when the point is in a corner of the desktop that the cursor cannot move past.</summary>
    bool IsAtStuckCorner(int x, int y);
    /// <summary>
    /// Copies a screen rectangle into <paramref name="pixels"/> (row-major, 0x00RRGGBB each; the
    /// array is reallocated only when the size changes). Returns false if the capture failed.
    /// </summary>
    bool TryCapture(int x, int y, int width, int height, ref int[] pixels);
}

public interface IWindowService
{
    IntPtr Find(WindowTarget target);
    bool IsAlive(IntPtr hwnd);
    bool IsForeground(IntPtr hwnd);
    /// <summary>Screen position of the window's client-area origin, in physical pixels.</summary>
    (int X, int Y) GetClientOrigin(IntPtr hwnd);
    /// <summary>True when the topmost window at a screen point belongs to this app.</summary>
    bool IsOwnWindowAt(int x, int y);
}
