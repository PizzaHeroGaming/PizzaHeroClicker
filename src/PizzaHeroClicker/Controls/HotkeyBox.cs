using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Controls;

/// <summary>
/// Click it, then press a key (with optional Ctrl/Alt/Shift/Win) to bind it.
/// Esc cancels, Backspace clears the binding.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty ComboProperty = DependencyProperty.Register(
        nameof(Combo), typeof(KeyCombo), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(default(KeyCombo), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyBox)d).Refresh()));

    /// <summary>
    /// Raised with true when any box starts listening and false when it stops. The app
    /// suspends its global hotkeys meanwhile, otherwise pressing an already-bound key would
    /// trigger its action instead of reaching the box.
    /// </summary>
    public static event Action<bool>? CaptureActiveChanged;

    /// <summary>Text shown while nothing is bound.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(HotkeyBox),
        new PropertyMetadata("None", (d, _) => ((HotkeyBox)d).Refresh()));

    private bool _capturing;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsTabStop = false; // capture starts on click only, never by tabbing through the form
        ContextMenu = null;
        IsUndoEnabled = false;
        ToolTip = "Click, then press a key. Esc cancels, Backspace clears.";
        Refresh();
    }

    public KeyCombo Combo
    {
        get => (KeyCombo)GetValue(ComboProperty);
        set => SetValue(ComboProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetCapturing(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetCapturing(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,           // Alt combinations and F10 arrive as "System"
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return; // wait for the real key
        }

        var mods = KeyMods.None;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= KeyMods.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= KeyMods.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= KeyMods.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= KeyMods.Win;

        if (mods == KeyMods.None && key == Key.Escape)
        {
            // cancel: keep the current binding
        }
        else if (mods == KeyMods.None && key == Key.Back)
        {
            Combo = default;
        }
        else
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0) Combo = new KeyCombo(vk, mods);
        }

        Keyboard.ClearFocus(); // ends capture via OnLostKeyboardFocus
        SetCapturing(false);
    }

    private void SetCapturing(bool value)
    {
        if (_capturing == value) return;
        _capturing = value;
        Refresh();
        CaptureActiveChanged?.Invoke(value);
    }

    private void Refresh() => Text = _capturing ? "Press a key…" : Combo.IsEmpty ? Placeholder : Combo.ToString();
}
