using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

/// <summary>Backs the action editor dialog. Works on a private copy of the action; the caller commits it.</summary>
public partial class ActionEditorViewModel : ObservableObject
{
    private readonly Func<Task<PickResult?>> _pickPoint;
    private readonly Func<Task<RegionPick?>> _pickRegion;
    private readonly Func<AreaWatchAction, Task<string?>> _capturePicture;
    private readonly Func<AreaWatchAction, Task> _testArea;
    private readonly Func<Task<(RegionPick Region, string Image)?>> _snipMarker;

    public ActionBase Action { get; }
    public string Title { get; }

    /// <summary>Looks at the screen with a bar-block action's settings and says what it sees. Set by the dialog host.</summary>
    public Func<BarBlockAction, Task>? TestBar { get; init; }

    /// <summary>Freezes the screen and asks for the centre, the bar and a marker on the still picture. Set by the dialog host.</summary>
    public Func<Task<BarSnapshotSetup?>>? SnapshotBar { get; init; }

    [RelayCommand]
    private async Task SetUpFromSnapshot()
    {
        if (IsPicking || Action is not BarBlockAction bar || SnapshotBar is null) return;
        IsPicking = true;
        try
        {
            if (await SnapshotBar() is not { } setup) return;
            (bar.CentreX, bar.CentreY) = (setup.Centre.X, setup.Centre.Y);
            bar.Radius = (int)Math.Round(Math.Sqrt(Math.Pow(setup.Bar.X - setup.Centre.X, 2) + Math.Pow(setup.Bar.Y - setup.Centre.Y, 2)));
            bar.BarColor = setup.Bar.Color.ToHex();
            bar.InnerRadius = Math.Min(bar.InnerRadius, Math.Max(0, bar.Radius - 20));
            if (setup.Marker is var (region, image))
            {
                (bar.AnchorX, bar.AnchorY, bar.AnchorWidth, bar.AnchorHeight) = (region.X, region.Y, region.Width, region.Height);
                bar.AnchorImage = image;
            }
            Error = "";
        }
        finally
        {
            IsPicking = false;
        }
    }

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isPicking;

    /// <summary>Bound to the "add key" box: as soon as a key is captured it joins the list and the box resets.</summary>
    [ObservableProperty] private KeyCombo _pendingKey;

    /// <param name="capturePicture">Snips a picture of a target from the screen; returns it as base64 PNG, or null.</param>
    /// <param name="testArea">Looks at the screen once with an area watch's current settings and shows what it found.</param>
    public ActionEditorViewModel(ActionBase action, string title, Func<Task<PickResult?>> pickPoint,
        Func<Task<RegionPick?>> pickRegion, Func<AreaWatchAction, Task<string?>> capturePicture, Func<AreaWatchAction, Task> testArea,
        Func<Task<(RegionPick Region, string Image)?>> snipMarker)
    {
        _snipMarker = snipMarker;
        _testArea = testArea;
        Action = action;
        Title = title;
        _pickPoint = pickPoint;
        _pickRegion = pickRegion;
        _capturePicture = capturePicture;
    }

    partial void OnPendingKeyChanged(KeyCombo value)
    {
        if (value.IsEmpty || Action is not KeyPressAction keys) return;
        keys.Keys.Add(value);
        PendingKey = default;
    }

    [RelayCommand]
    private void RemoveKey(object? parameter)
    {
        if (Action is KeyPressAction keys && parameter is KeyCombo combo) keys.Keys.Remove(combo);
    }

    [RelayCommand]
    private void RemoveRule(object? parameter)
    {
        if (Action is AreaWatchAction area && parameter is AreaRule rule) area.Rules.Remove(rule);
        if (Action is BarBlockAction bar && parameter is BlockTarget target) bar.Targets.Remove(target);
    }

    /// <summary>Moves a thing to block one place up the list: higher up = more valuable.</summary>
    [RelayCommand]
    private void MoveTargetUp(object? parameter)
    {
        if (Action is not BarBlockAction bar || parameter is not BlockTarget target) return;
        int i = bar.Targets.IndexOf(target);
        if (i > 0) bar.Targets.Move(i, i - 1);
    }

    /// <summary>Fills in the four kinds of radiation from Galaxy Idle Clicker's Radiations game.</summary>
    [RelayCommand]
    private void UseRadiationColours()
    {
        if (Action is not BarBlockAction bar) return;
        bar.Targets.Clear();
        foreach (var target in BarBlockAction.RadiationDefaults()) bar.Targets.Add(target);
    }

    [RelayCommand]
    private async Task TestBarBlock()
    {
        if (IsPicking || Action is not BarBlockAction bar || TestBar is null) return;
        Error = ValidateAction(bar) ?? "";
        if (Error.Length > 0) return;
        IsPicking = true;
        try
        {
            await TestBar(bar);
        }
        finally
        {
            IsPicking = false;
        }
    }

    /// <summary>Picks a point on screen and writes it into the fields named by <paramref name="target"/>.</summary>
    [RelayCommand]
    private async Task PickPosition(string? target)
    {
        if (IsPicking) return;
        IsPicking = true;
        try
        {
            var pick = await _pickPoint();
            if (pick is null) return;

            switch (Action)
            {
                case ClickAction a:
                    (a.X, a.Y, a.UseCursor) = (pick.X, pick.Y, false);
                    break;
                case ScrollAction a:
                    (a.X, a.Y, a.UseCursor) = (pick.X, pick.Y, false);
                    break;
                case MoveAction a:
                    (a.X, a.Y) = (pick.X, pick.Y);
                    break;
                case DragAction a when target == "end":
                    (a.X2, a.Y2) = (pick.X, pick.Y);
                    break;
                case DragAction a:
                    (a.X1, a.Y1) = (pick.X, pick.Y);
                    break;
                case PixelClickAction a when target == "click":
                    (a.ClickX, a.ClickY, a.ClickAtPixel) = (pick.X, pick.Y, false);
                    break;
                case PixelActionBase a: // the eyedropper: position and colour together
                    (a.X, a.Y, a.Color) = (pick.X, pick.Y, pick.Color.ToHex());
                    break;
                case AreaWatchAction a: // the eyedropper adds a colour rule; only the colour matters
                    a.Rules.Add(new AreaRule { Color = pick.Color.ToHex() });
                    break;
                case BarBlockAction a when target == "centre":
                    (a.CentreX, a.CentreY) = (pick.X, pick.Y);
                    break;
                case BarBlockAction a when target == "bar":
                    // The bar sets both how far out it travels and what colour to look for.
                    if (a.CentreX == 0 && a.CentreY == 0)
                    {
                        Error = "Pick the centre first, then the bar.";
                        break;
                    }
                    a.Radius = (int)Math.Round(Math.Sqrt(Math.Pow(pick.X - a.CentreX, 2) + Math.Pow(pick.Y - a.CentreY, 2)));
                    a.BarColor = pick.Color.ToHex();
                    a.InnerRadius = Math.Min(a.InnerRadius, Math.Max(0, a.Radius - 20));
                    break;
                case BarBlockAction a: // the eyedropper adds something to block, by its colour
                    a.Targets.Add(new BlockTarget { Color = pick.Color.ToHex() });
                    break;
            }
        }
        finally
        {
            IsPicking = false;
        }
    }

    /// <summary>Lets the user drag a rectangle on screen for an area watch.</summary>
    [RelayCommand]
    private async Task PickRegion()
    {
        if (IsPicking || Action is not AreaWatchAction area) return;
        IsPicking = true;
        try
        {
            var region = await _pickRegion();
            if (region is null) return;
            (area.X, area.Y, area.Width, area.Height) = (region.X, region.Y, region.Width, region.Height);
        }
        finally
        {
            IsPicking = false;
        }
    }

    /// <summary>Snips a picture of a target and adds it to an area watch as a "looks like this" rule.</summary>
    [RelayCommand]
    private async Task AddPicture()
    {
        if (IsPicking || Action is not AreaWatchAction area) return;
        IsPicking = true;
        try
        {
            string? image = await _capturePicture(area);
            if (image is not null) area.Rules.Add(new AreaRule { Image = image });
        }
        finally
        {
            IsPicking = false;
        }
    }

    /// <summary>Snips a fixed piece of the screen to act as the area watch's start/stop marker.</summary>
    [RelayCommand]
    private async Task SnipMarker()
    {
        if (IsPicking || Action is not (AreaWatchAction or BarBlockAction)) return;
        IsPicking = true;
        try
        {
            if (await _snipMarker() is not var (region, image)) return;
            switch (Action)
            {
                case AreaWatchAction area:
                    (area.AnchorX, area.AnchorY, area.AnchorWidth, area.AnchorHeight) = (region.X, region.Y, region.Width, region.Height);
                    area.AnchorImage = image;
                    break;
                case BarBlockAction bar:
                    (bar.AnchorX, bar.AnchorY, bar.AnchorWidth, bar.AnchorHeight) = (region.X, region.Y, region.Width, region.Height);
                    bar.AnchorImage = image;
                    break;
            }
        }
        finally
        {
            IsPicking = false;
        }
    }

    [RelayCommand]
    private void ClearMarker()
    {
        if (Action is AreaWatchAction area) area.AnchorImage = "";
        if (Action is BarBlockAction bar) bar.AnchorImage = "";
    }

    /// <summary>Runs the area watch against the screen once, without clicking, and reports what it saw.</summary>
    [RelayCommand]
    private async Task TestArea()
    {
        if (IsPicking || Action is not AreaWatchAction area) return;
        Error = ValidateAction(area) ?? "";
        if (Error.Length > 0) return;
        IsPicking = true;
        try
        {
            await _testArea(area);
        }
        finally
        {
            IsPicking = false;
        }
    }

    /// <summary>Returns true when the action can be saved; otherwise sets <see cref="Error"/>.</summary>
    public bool Validate()
    {
        Error = ValidateAction(Action) ?? "";
        if (Error.Length > 0) return false;
        Action.Normalize();
        return true;
    }

    /// <summary>Shared with the main view model so a hand-edited profile gets the same checks.</summary>
    public static string? ValidateAction(ActionBase action)
    {
        if (action.IntervalMs is int lo && action.IntervalMaxMs is int hi && hi < lo)
            return "The maximum delay must not be smaller than the delay.";
        return action switch
        {
            KeyPressAction { Keys.Count: 0 } => "Add at least one key.",
            PixelActionBase p when !PixelColor.TryParse(p.Color, out _) => "The colour must look like #RRGGBB.",
            AreaWatchAction { Rules.Count: 0 } => "Add at least one picture or colour to look for.",
            AreaWatchAction a when a.Rules.All(r => r.DontClick) => "Every picture and colour is set to Don't click, so nothing would ever be clicked.",
            AreaWatchAction a when a.Rules.Any(r => !r.IsImage && !PixelColor.TryParse(r.Color, out _)) => "Every colour must look like #RRGGBB.",
            AreaWatchAction a when a.Width < 1 || a.Height < 1 => "Select an area to watch.",
            BarBlockAction { CentreX: 0, CentreY: 0 } => "Pick the centre of the circle the bar travels round.",
            BarBlockAction b when !PixelColor.TryParse(b.BarColor, out _) => "The bar's colour must look like #RRGGBB.",
            BarBlockAction { Targets.Count: 0 } => "Add at least one colour to block (or use the radiation colours).",
            BarBlockAction b when b.Targets.Any(t => !PixelColor.TryParse(t.Color, out _)) => "Every colour must look like #RRGGBB.",
            BarBlockAction b when b.ClockwiseKey.IsEmpty || b.AnticlockwiseKey.IsEmpty || b.ClockwiseKey == b.AnticlockwiseKey => "Set two different keys for turning the bar.",
            _ => null,
        };
    }
}
