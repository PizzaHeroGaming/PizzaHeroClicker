using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

// The action list: add, edit, reorder, duplicate, delete, and position capture.
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditActionCommand), nameof(DuplicateActionCommand), nameof(DeleteActionCommand),
        nameof(MoveActionUpCommand), nameof(MoveActionDownCommand))]
    private ActionBase? _selectedAction;

    private bool HasSelection => SelectedAction is not null;

    public string CaptureButtonText =>
        Profile.Hotkeys.Capture.IsEmpty ? "CAPTURE CLICK" : $"CAPTURE CLICK  [{Profile.Hotkeys.Capture}]";

    /// <summary>
    /// Screen position of the target window's client area when the profile uses
    /// window-relative coordinates; (0, 0) in absolute mode; null if the window can't be found.
    /// </summary>
    private (int X, int Y)? CoordinateOrigin()
    {
        var target = Profile.Window;
        if (target is not { Enabled: true, RelativeCoordinates: true }) return (0, 0);
        IntPtr hwnd = _s.Windows.Find(target);
        return hwnd == IntPtr.Zero ? null : _s.Windows.GetClientOrigin(hwnd);
    }

    /// <summary>Picks a screen point and converts it into the profile's coordinate space.</summary>
    private async Task<PickResult?> PickInProfileSpaceAsync()
    {
        var pick = await _s.Dialogs.PickPointAsync();
        if (pick is null) return null;
        if (CoordinateOrigin() is not var (ox, oy))
        {
            Footer = "The target window isn't open, so a window-relative position can't be captured.";
            return null;
        }
        return pick with { X = pick.X - ox, Y = pick.Y - oy };
    }

    /// <summary>Lets the user drag a rectangle on screen and converts it into the profile's coordinate space.</summary>
    private async Task<RegionPick?> PickRegionInProfileSpaceAsync()
    {
        var region = await _s.Dialogs.PickRegionAsync();
        if (region is null) return null;
        if (CoordinateOrigin() is not var (ox, oy))
        {
            Footer = "The target window isn't open, so a window-relative area can't be captured.";
            return null;
        }
        return region with { X = region.X - ox, Y = region.Y - oy };
    }

    [RelayCommand]
    private void AddAction(string? type)
    {
        ActionBase action = type switch
        {
            "key" => new KeyPressAction(),
            "wait" => new WaitAction(),
            "scroll" => new ScrollAction(),
            "drag" => new DragAction(),
            "move" => new MoveAction(),
            "pixelWait" => new PixelWaitAction(),
            "pixelClick" => new PixelClickAction(),
            "areaWatch" => new AreaWatchAction(),
            _ => new ClickAction { Button = Profile.DefaultButton, Kind = Profile.DefaultKind },
        };
        if (!_s.Dialogs.EditAction(action, $"Add {action.TypeName.ToLowerInvariant()} action", PickInProfileSpaceAsync, PickRegionInProfileSpaceAsync)) return;
        InsertAfterSelection(action);
    }

    private void InsertAfterSelection(ActionBase action)
    {
        var list = Profile.Actions;
        int index = SelectedAction is null ? list.Count : list.IndexOf(SelectedAction) + 1;
        list.Insert(Math.Clamp(index, 0, list.Count), action);
        SelectedAction = action;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditAction()
    {
        if (SelectedAction is not { } original) return;
        var copy = original.Clone();
        if (!_s.Dialogs.EditAction(copy, $"Edit {copy.TypeName.ToLowerInvariant()} action", PickInProfileSpaceAsync, PickRegionInProfileSpaceAsync)) return;

        int index = Profile.Actions.IndexOf(original);
        if (index < 0) return;
        Profile.Actions[index] = copy; // replacing the item refreshes its row in the list
        SelectedAction = copy;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateAction()
    {
        if (SelectedAction is { } action) InsertAfterSelection(action.Clone());
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteAction()
    {
        if (SelectedAction is not { } action) return;
        var list = Profile.Actions;
        int index = list.IndexOf(action);
        list.Remove(action);
        SelectedAction = list.Count == 0 ? null : list[Math.Clamp(index, 0, list.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveActionUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveActionDown() => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (SelectedAction is null) return;
        int from = Profile.Actions.IndexOf(SelectedAction);
        MoveAction(from, from + delta);
    }

    /// <summary>Reorders the list (also called by drag and drop).</summary>
    public void MoveAction(int from, int to)
    {
        var list = Profile.Actions;
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return;
        var moved = list[from];
        list.Move(from, to);
        SelectedAction = moved;
    }

    [RelayCommand]
    private void ClearActions()
    {
        if (Profile.Actions.Count == 0) return;
        if (!_s.Dialogs.Confirm("Clear actions", $"Remove all {Profile.Actions.Count} actions from this profile?", "Clear")) return;
        Profile.Actions.Clear();
        SelectedAction = null;
    }

    /// <summary>Called by the view after an in-row edit (the enabled check box).</summary>
    public void NotifyActionEdited() => OnProfileEdited(Profile, new System.ComponentModel.PropertyChangedEventArgs(nameof(Profile.Actions)));

    /// <summary>
    /// The capture hotkey: finishes an on-screen pick if one is active, otherwise appends a
    /// click action at the cursor's current position.
    /// </summary>
    [RelayCommand]
    private void CaptureAtCursor()
    {
        if (_s.Dialogs.TryAcceptPickAtCursor()) return;

        var (x, y) = _s.Input.GetCursor();
        if (CoordinateOrigin() is not var (ox, oy))
        {
            Footer = "The target window isn't open, so a window-relative position can't be captured.";
            return;
        }
        var action = new ClickAction { X = x - ox, Y = y - oy, Button = Profile.DefaultButton, Kind = Profile.DefaultKind };
        Profile.Actions.Add(action);
        SelectedAction = action;
        Footer = $"Captured click #{Profile.Actions.Count} at ({action.X}, {action.Y}).";
    }

    /// <summary>"Capture by clicking": pick a point on screen with the mouse and add it as a click action.</summary>
    [RelayCommand]
    private async Task CaptureByClick()
    {
        var pick = await PickInProfileSpaceAsync();
        if (pick is null) return;
        var action = new ClickAction { X = pick.X, Y = pick.Y, Button = Profile.DefaultButton, Kind = Profile.DefaultKind };
        Profile.Actions.Add(action);
        SelectedAction = action;
        Footer = $"Captured click #{Profile.Actions.Count} at ({action.X}, {action.Y}).";
    }
}
