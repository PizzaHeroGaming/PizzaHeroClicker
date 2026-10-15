using System.Windows;
using Microsoft.Win32;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using PizzaHeroClicker.ViewModels;

namespace PizzaHeroClicker.Views;

/// <summary>The WPF implementation of <see cref="IDialogService"/>.</summary>
public sealed class DialogService(ScreenService screen) : IDialogService
{
    /// <summary>Name of the capture hotkey, shown in the picker's hint. Set by the view model.</summary>
    public Func<string?> CaptureKeyName { get; set; } = () => null;

    /// <summary>The window new dialogs should sit on top of: the active dialog if any, else the main window.</summary>
    private static Window? Owner
    {
        get
        {
            var app = Application.Current;
            var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w is not PickerWindow);
            if (active is not null) return active;
            return app.MainWindow is { IsVisible: true } main ? main : null;
        }
    }

    private static bool? ShowModal(Window dialog)
    {
        if (Owner is { } owner) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog();
    }

    public bool EditAction(ActionBase action, string title, Func<Task<PickResult?>> pickPoint, Func<Task<RegionPick?>> pickRegion) =>
        ShowModal(new ActionEditorWindow(new ActionEditorViewModel(action, title, pickPoint, pickRegion, CaptureTargetPictureAsync, TestAreaWatchAsync, SnipMarkerAsync)
        {
            TestBar = TestBarBlockAsync,
        })) == true;

    /// <summary>
    /// Watches the bar's circle for about three seconds with a bar-block action's settings and
    /// says what it saw: the bar, and anything flying out. No keys are pressed.
    /// </summary>
    public async Task TestBarBlockAsync(BarBlockAction action)
    {
        const string title = "Block test";
        string message;
        var hidden = await HideOwnWindowsAsync();
        try
        {
            var copy = (BarBlockAction)action.Clone();
            copy.Normalize();
            if (CoordinateOrigin() is not var (ox, oy))
            {
                message = "The target window isn't open, so the circle can't be located.";
            }
            else
            {
                var (x, y, size) = copy.LookBox;
                var detector = new BarBlockDetector(copy);
                int[] pixels = [];
                var seen = new int[copy.Targets.Count];
                int looks = 0, barLooks = 0;
                double? lastBar = null;
                int lastBarPixels = 0;
                long start = Environment.TickCount64;
                while (Environment.TickCount64 - start < 3200)
                {
                    if (screen.TryCapture(x + ox, y + oy, size, size, ref pixels))
                    {
                        looks++;
                        detector.Look(pixels, Environment.TickCount64 - start);
                        if (detector.BarAngle is double angle) (barLooks, lastBar, lastBarPixels) = (barLooks + 1, angle, detector.BarPixels);
                        foreach (var group in detector.Threats.GroupBy(t => t.Target))
                            seen[group.Key] = Math.Max(seen[group.Key], group.Count());
                    }
                    await Task.Delay(50);
                }
                var text = new System.Text.StringBuilder();
                if (looks == 0) text.AppendLine("That part of the screen could not be captured.");
                else if (lastBar is double bar)
                    text.AppendLine($"Bar: found in {barLooks} of {looks} looks, last at {bar:0} degrees ({lastBarPixels} pixels of its colour on its circle).");
                else
                    text.AppendLine("Bar: NOT found on its circle. Click PICK THE BAR again, right on the bar, so its circle and colour are set from it.");
                if (looks > 0)
                {
                    var kinds = copy.Targets.Select((t, i) => seen[i] > 0 ? $"{(t.Name.Length > 0 ? t.Name : t.Color)} ({seen[i]} at once)" : null).Where(s => s is not null).ToList();
                    text.AppendLine(kinds.Count > 0
                        ? "Flying out, seen: " + string.Join(", ", kinds) + "."
                        : "Nothing seen flying out. That is expected if the game was not sending anything during those three seconds; it only notices things once they move.");
                }
                message = text.ToString();
            }
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
        }
        finally
        {
            RestoreWindows(hidden);
        }
        Inform(title, message);
    }

    /// <summary>
    /// Lets the user snip a fixed piece of the screen to use as a start/stop marker. Returns where
    /// it is (in the profile's coordinate space) and its picture, or null if cancelled or unusable.
    /// </summary>
    public async Task<(RegionPick Region, string Image)?> SnipMarkerAsync()
    {
        const string title = "Start / stop marker";
        Snip? snip;
        var hidden = await HideOwnWindowsAsync();
        try
        {
            snip = await RegionPickerWindow.SnipAsync(screen,
                "Drag a box around something that is ALWAYS showing during the game and gone afterwards (a label, a logo).   Esc cancels.");
        }
        finally
        {
            RestoreWindows(hidden);
        }
        if (snip is null) return null;

        var (w, h) = (snip.Region.Width, snip.Region.Height);
        if (w < ScreenAnchor.MinSide || h < ScreenAnchor.MinSide || w > ScreenAnchor.MaxSide || h > ScreenAnchor.MaxSide)
        {
            Inform(title, $"That box is {w} x {h} pixels. A marker should be between {ScreenAnchor.MinSide} and {ScreenAnchor.MaxSide} pixels each way: a label or an icon, not the whole screen.");
            return null;
        }
        if (CoordinateOrigin() is not var (ox, oy))
        {
            Inform(title, "The target window isn't open, so the marker's position can't be stored relative to it.");
            return null;
        }
        return (snip.Region with { X = snip.Region.X - ox, Y = snip.Region.Y - oy }, TemplateImage.Encode(snip.Pixels, w, h));
    }


    /// <summary>
    /// Screen position of the profile's coordinate origin (the target window's corner in
    /// window-relative mode, otherwise 0,0); null if that window isn't open. Set by the view model.
    /// </summary>
    public Func<(int X, int Y)?> CoordinateOrigin { get; set; } = () => (0, 0);

    /// <summary>
    /// Hides every window of this app so it is not in a screen capture, and waits for the desktop
    /// to repaint. The windows are hidden at the Win32 level: calling WPF's Hide() on a modal
    /// dialog would end the dialog.
    /// </summary>
    private static async Task<(List<IntPtr> Windows, IntPtr Active)> HideOwnWindowsAsync()
    {
        var hidden = new List<IntPtr>();
        IntPtr active = IntPtr.Zero;
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsVisible) continue;
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) continue;
            if (window.IsActive) active = hwnd;
            hidden.Add(hwnd);
            Native.NativeMethods.ShowWindow(hwnd, Native.NativeMethods.SW_HIDE);
        }
        await Task.Delay(300);
        return (hidden, active);
    }

    private static void RestoreWindows((List<IntPtr> Windows, IntPtr Active) hidden)
    {
        foreach (IntPtr hwnd in hidden.Windows) Native.NativeMethods.ShowWindow(hwnd, Native.NativeMethods.SW_SHOWNA);
        if (hidden.Active != IntPtr.Zero) Native.NativeMethods.SetForegroundWindow(hidden.Active);
    }

    /// <summary>
    /// The editor's Test button: looks at the area once, exactly as a run would, and says what it
    /// saw and what it would click. Nothing is clicked.
    /// </summary>
    public async Task TestAreaWatchAsync(AreaWatchAction action)
    {
        const string title = "Area watch test";
        string message;
        var hidden = await HideOwnWindowsAsync();
        try
        {
            var copy = (AreaWatchAction)action.Clone();
            copy.Normalize();
            if (CoordinateOrigin() is not var (ox, oy))
            {
                message = "The target window isn't open, so the area can't be located.";
            }
            else
            {
                int[] pixels = [];
                if (!screen.TryCapture(copy.X + ox, copy.Y + oy, copy.Width, copy.Height, ref pixels))
                {
                    message = "That part of the screen could not be captured.";
                }
                else
                {
                    string marker = "";
                    if (copy.HasAnchor && TemplateImage.TryDecode(copy.AnchorImage, out var reference, out int mw, out int mh))
                    {
                        int[] live = [];
                        double alike = screen.TryCapture(copy.AnchorX + ox, copy.AnchorY + oy, mw, mh, ref live) ? ScreenAnchor.Similarity(live, reference) : 0;
                        marker = $"Start/stop marker: {(alike * 100 >= copy.AnchorMatchPercent ? "SHOWING" : "not showing")} ({alike:P0} alike, needs {copy.AnchorMatchPercent}%).\n\n";
                        if (alike * 100 < copy.AnchorMatchPercent)
                        {
                            // Maybe it is there, just not exactly where it was snipped (the game window moved).
                            const int reach = 40;
                            int rw = mw + 2 * reach, rh = mh + 2 * reach;
                            int[] region = [];
                            if (screen.TryCapture(copy.AnchorX + ox - reach, copy.AnchorY + oy - reach, rw, rh, ref region)
                                && ScreenAnchor.FindNear(region, rw, rh, reference, mw, mh, copy.AnchorMatchPercent / 100.0) is var (fx, fy, share))
                            {
                                int dx = fx - reach, dy = fy - reach;
                                marker = $"Start/stop marker: SHOWING, but {Math.Abs(dx)} px {(dx < 0 ? "left" : "right")} and {Math.Abs(dy)} px {(dy < 0 ? "up" : "down")} of where it was snipped ({share:P0} alike). " +
                                         "The game window has probably moved. A run finds it there and moves the watched area to match; snipping the marker again (or turning on window-relative positions on the Window tab) puts everything back in line.\n\n";
                            }
                        }
                    }
                    var detector = new AreaDetector(copy) { CollectReport = true };
                    detector.Find(pixels);
                    // A busy backdrop is judged by movement, which takes a few seconds of watching.
                    long until = Environment.TickCount64 + 4000;
                    while (detector.LastReport is { BusyScene: true } && Environment.TickCount64 < until)
                    {
                        await Task.Delay(100);
                        if (screen.TryCapture(copy.X + ox, copy.Y + oy, copy.Width, copy.Height, ref pixels)) detector.Find(pixels);
                    }
                    message = marker + detector.DescribeLastReport();
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
        }
        finally
        {
            RestoreWindows(hidden);
        }
        Inform(title, message);
    }

    /// <summary>
    /// Freezes the screen and lets the user snip a picture of one target. Returns it as base64
    /// PNG, or null if cancelled or unusable (the user is told why).
    ///
    /// On a busy backdrop the area is watched for about three seconds first, so that the
    /// unchanging scene can be cut away and the picture shows only the moving target.
    /// </summary>
    public async Task<string?> CaptureTargetPictureAsync(AreaWatchAction area)
    {
        const string title = "Picture of a target";
        Snip? snip;
        SceneModel? scene = null;
        int[] areaFrame = [];
        var (areaX, areaY, areaW, areaH) = (0, 0, 0, 0);

        var hidden = await HideOwnWindowsAsync();
        try
        {
            if (area.Width >= 30 && area.Height >= 30 && CoordinateOrigin() is var (ox, oy))
            {
                (areaX, areaY, areaW, areaH) = (area.X + ox, area.Y + oy, area.Width, area.Height);
                int[] pixels = [];
                if (screen.TryCapture(areaX, areaY, areaW, areaH, ref pixels) && AreaDetector.IsBusyScene(pixels, areaW, areaH))
                {
                    scene = new SceneModel();
                    long until = Environment.TickCount64 + 3200;
                    while (Environment.TickCount64 < until)
                    {
                        if (screen.TryCapture(areaX, areaY, areaW, areaH, ref pixels)) scene.Update(pixels, areaW, areaH, Environment.TickCount64);
                        await Task.Delay(100);
                    }
                }
            }

            string hint = scene is null
                ? "The screen is frozen. Drag a box around ONE target.   Esc or right-click cancels."
                : "The screen is frozen. Drag a box around ONE target; the scene behind it will be removed.   Esc or right-click cancels.";
            snip = await RegionPickerWindow.SnipAsync(screen, hint, (frame, vx, vy, vw, vh) =>
            {
                // Keep the watched area exactly as it is in the frozen frame, to compare with the learned scene.
                if (scene is null || areaX < vx || areaY < vy || areaX + areaW > vx + vw || areaY + areaH > vy + vh) return;
                areaFrame = new int[areaW * areaH];
                for (int y = 0; y < areaH; y++) Array.Copy(frame, (areaY - vy + y) * vw + areaX - vx, areaFrame, y * areaW, areaW);
            });
        }
        finally
        {
            RestoreWindows(hidden);
        }
        if (snip is null) return null;

        if (scene is { Ready: true } && areaFrame.Length > 0)
        {
            int kept = scene.KeyOutScene(snip.Pixels, snip.Region.X, snip.Region.Y, snip.Region.Width, snip.Region.Height,
                areaFrame, areaX, areaY, areaW, areaH);
            if (kept < 40)
            {
                Inform(title, "Nothing in that box was moving, so there is no target to keep. On a busy backdrop only moving things can be picked out: snip a target while it is flying, inside the watched area.");
                return null;
            }
        }

        var (w, h) = (snip.Region.Width, snip.Region.Height);
        if (w < TemplateImage.MinSide || h < TemplateImage.MinSide)
        {
            Inform(title, "That box is too small. Drag it around the whole target.");
            return null;
        }
        if (w > TemplateImage.MaxSide || h > TemplateImage.MaxSide)
        {
            Inform(title, $"That box is {w} x {h} pixels; the limit is {TemplateImage.MaxSide} each way. Drag a snug box around a single target.");
            return null;
        }
        if (PreparedTemplate.Create(snip.Pixels, w, h) is null)
        {
            Inform(title, "That picture is a single flat colour, so there is no shape to recognise. Include the whole target and a little of the background around it.");
            return null;
        }
        return TemplateImage.Encode(snip.Pixels, w, h);
    }

    public Task<RegionPick?> PickRegionAsync() => RegionPickerWindow.PickAsync();

    public Task<PickResult?> PickPointAsync() => PickerWindow.PickAsync(screen, CaptureKeyName());

    public bool TryAcceptPickAtCursor()
    {
        if (PickerWindow.Current is not { } picker) return false;
        picker.AcceptAtCursor();
        return true;
    }

    public bool Confirm(string title, string message, string confirmText = "OK") =>
        PromptWindow.Show(Owner, title, message, null, confirmText, "Cancel").Button == 0;

    public int Choose(string title, string message, params string[] buttons) =>
        PromptWindow.Show(Owner, title, message, null, buttons).Button;

    public string? Prompt(string title, string message, string initialValue = "")
    {
        var (button, text) = PromptWindow.Show(Owner, title, message, initialValue, "OK", "Cancel");
        return button == 0 ? text.Trim() : null;
    }

    public void Inform(string title, string message) => PromptWindow.Show(Owner, title, message, null, "OK");

    public int ReviewProfile(string title, string profileName, ProfileReview review, bool importing)
    {
        var dialog = new ProfileReviewWindow(title, profileName, review, importing);
        ShowModal(dialog);
        return dialog.Result;
    }

    public string? ChooseGame(string title, string message, IReadOnlyList<string> games, string current, string noGameLabel, string confirmText)
    {
        var dialog = new GamePickerWindow(title, message, games, current, noGameLabel, confirmText);
        return ShowModal(dialog) == true ? dialog.Result : null;
    }

    public WindowInfo? PickWindow(Func<IReadOnlyList<WindowInfo>> listWindows)
    {
        var dialog = new WindowPickerWindow(listWindows);
        return ShowModal(dialog) == true ? dialog.Selected : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
