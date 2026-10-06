using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

// Window targeting, the tray icon, and the two overlays.
public partial class MainViewModel
{
    public const string HkOverlay = "overlay";

    private int _statusTicks;
    private string _lastTrayState = "";

    private void InitializeWindowFeatures()
    {
        _s.Tray.ToggleRequested += ToggleRun;
        _s.Tray.ProfileRequested += profile => LoadProfile(profile);
        _s.Tray.MenuState = () => new TrayMenuState(_s.Engine.IsActive, _s.Profiles.List(), CurrentRef);

        _s.Overlays.StatusMoved += (x, y) =>
        {
            Profile.Overlay.StatusX = x;
            Profile.Overlay.StatusY = y;
        };
    }

    // ------------------------------------------------------------------ target window

    [RelayCommand]
    private void PickTargetWindow()
    {
        var picked = _s.Dialogs.PickWindow(_s.Windows.ListWindows);
        if (picked is null) return;
        Profile.Window.Title = picked.Title;
        Profile.Window.ProcessName = picked.ProcessName;
        Profile.Window.Enabled = true;
        Footer = $"Target window set to \"{picked.Title}\".";
    }

    [RelayCommand]
    private void ClearTargetWindow()
    {
        Profile.Window.Enabled = false;
        Profile.Window.RelativeCoordinates = false;
        Profile.Window.Title = "";
        Profile.Window.ProcessName = "";
    }

    /// <summary>
    /// When window-relative coordinates are switched on or off, shift every saved position by
    /// the window's current origin so the actions keep pointing at the same spots.
    /// </summary>
    private void OnWindowSettingEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WindowTarget target) return;
        bool relativeToggled = e.PropertyName == nameof(WindowTarget.RelativeCoordinates) && target.Enabled;
        bool enabledToggled = e.PropertyName == nameof(WindowTarget.Enabled) && target.RelativeCoordinates;
        if (!relativeToggled && !enabledToggled) return;
        if (Profile.Actions.Count == 0) return;

        bool nowRelative = target is { Enabled: true, RelativeCoordinates: true };
        IntPtr hwnd = _s.Windows.Find(target);
        if (hwnd == IntPtr.Zero)
        {
            Footer = "The target window isn't open, so existing positions were left unchanged. Re-capture them if they look wrong.";
            return;
        }

        var (ox, oy) = _s.Windows.GetClientOrigin(hwnd);
        int sign = nowRelative ? -1 : 1;
        foreach (var action in Profile.Actions) action.Offset(sign * ox, sign * oy);
        Footer = nowRelative
            ? "Positions converted to window-relative coordinates."
            : "Positions converted back to screen coordinates.";
    }

    // ------------------------------------------------------------------ overlays and tray

    private void TogglePositionOverlay() => Profile.Overlay.ShowPositions = !Profile.Overlay.ShowPositions;

    partial void AfterProfileLoaded() => UpdateOverlays(force: true);

    partial void AfterStatusRefreshed(EngineSnapshot snapshot)
    {
        UpdateOverlays(force: false);

        string trayState = $"{StateKind}|{CurrentRef.Key}";
        if (trayState != _lastTrayState)
        {
            _lastTrayState = trayState;
            string state = StateKind == "Countdown" ? "Starting" : char.ToUpperInvariant(StateText[0]) + StateText[1..].ToLowerInvariant();
            _s.Tray.SetTooltip($"Pizza Hero Clicker\n{state} - {CurrentRef.Display}");
        }
    }

    private void UpdateOverlays(bool force)
    {
        var overlay = Profile.Overlay;
        string text = StateKind switch
        {
            "Running" => "ON",
            "Paused" => "PAUSED",
            "Waiting" => "WAITING",
            "Countdown" => StateText.Replace("STARTING IN ", ""),
            _ => "OFF",
        };
        _s.Overlays.UpdateStatus(overlay.ShowStatus, overlay.StatusClickThrough, StateKind, text, overlay.StatusX, overlay.StatusY);

        if (!overlay.ShowPositions)
        {
            _s.Overlays.UpdatePositions(false, []);
            return;
        }

        // Markers follow the target window in relative mode, so re-resolve them twice a second.
        if (!force && ++_statusTicks % 5 != 0) return;
        _s.Overlays.UpdatePositions(true, BuildMarkers());
    }

    private List<OverlayMarker> BuildMarkers()
    {
        var markers = new List<OverlayMarker>();
        if (CoordinateOrigin() is not var (ox, oy)) return markers; // relative mode and the window is closed

        for (int i = 0; i < Profile.Actions.Count; i++)
        {
            int n = 0;
            foreach (var (x, y) in Profile.Actions[i].GetPoints())
            {
                // A drag or pixel click has two points: "6" and "6b".
                string label = (i + 1) + (n++ == 0 ? "" : "b");
                markers.Add(new OverlayMarker(label, x + ox, y + oy));
            }
        }
        return markers;
    }
}
