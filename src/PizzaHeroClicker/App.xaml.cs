using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using PizzaHeroClicker.ViewModels;
using PizzaHeroClicker.Views;

namespace PizzaHeroClicker;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private bool _started;
    private string? _snapshotDir; // developer aid: `--snapshot <dir>` renders every tab to PNG and exits
    private bool _selfTest;       // developer aid: `--selftest` exercises real input end to end, logs results, exits

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int flag = Array.IndexOf(e.Args, "--snapshot");
        if (flag >= 0 && flag + 1 < e.Args.Length) _snapshotDir = e.Args[flag + 1];
        _selfTest = e.Args.Contains("--selftest");

        Log.Init(AppPaths.Logs);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Two instances would fight over the same global hotkeys and profile files.
        _singleInstance = new Mutex(initiallyOwned: true, "PizzaHeroClicker.SingleInstance", out bool isFirst);
        if (!isFirst && _snapshotDir is null && !_selfTest)
        {
            MessageBox.Show("Pizza Hero Clicker is already running. Look for it in the system tray.", "Pizza Hero Clicker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Info($"Starting v{typeof(App).Assembly.GetName().Version} (portable: {AppPaths.IsPortable}, data: {AppPaths.Root})");
        TimingEngine.DisablePowerThrottling();

        _services = new AppServices();
        Log.Info($"High-resolution timer available: {_services.Timing.IsHighResolution}");
        _viewModel = new MainViewModel(_services);

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();

        _services.Tray.ShowRequested += window.Restore;
        _services.Tray.ExitRequested += window.Close;
        _services.Tray.Show();

        if (_services.Settings.StartMinimized && _snapshotDir is null)
        {
            // To the tray if that is enabled (the window is simply never shown), otherwise to the taskbar.
            if (!_services.Settings.MinimizeToTray)
            {
                window.WindowState = WindowState.Minimized;
                window.Show();
            }
        }
        else
        {
            window.Show();
        }
        _started = true;

        _viewModel.LaunchInstaller = RunInstallerAndExit;
        if (_snapshotDir is null && !_selfTest) _ = _viewModel.CheckForUpdatesQuietlyAsync();

        if (_snapshotDir is not null) _ = RunSnapshotAsync(window, _snapshotDir);
        else if (_selfTest) _ = RunSelfTestAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Shutdown();
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }
        Log.Info("Exited.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;

        if (_snapshotDir is not null || _selfTest || !_started)
        {
            // Nothing usable on screen yet: report and quit instead of leaving a ghost process.
            if (_snapshotDir is null && !_selfTest)
                MessageBox.Show("Pizza Hero Clicker could not start:\n\n" + e.Exception.Message, "Pizza Hero Clicker",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Keep the app alive; a stray UI error should not kill a long-running session.
        _services?.Engine.Stop();
        MessageBox.Show("Something went wrong, and any active run was stopped. Details were written to the log.\n\n" + e.Exception.Message,
            "Pizza Hero Clicker", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Hands over to a downloaded installer: it upgrades the app in place and starts it again.
    /// The single-instance mutex is let go first, because Setup refuses to run while it exists.
    /// </summary>
    private void RunInstallerAndExit(string installer)
    {
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: nothing to let go of.
        }
        _singleInstance?.Dispose();
        _singleInstance = null;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, "/SILENT /NORESTART /RELAUNCH=1") { UseShellExecute = true });
        _viewModel?.DiscardChanges(); // already asked about unsaved changes before downloading
        Shutdown();
    }

    /// <summary>Exit from a developer mode: drop its scratch edits so no "save changes?" prompt appears.</summary>
    private void Quit(int exitCode)
    {
        _viewModel?.DiscardChanges();
        Shutdown(exitCode);
    }

    // ------------------------------------------------------------------ developer self-test

    /// <summary>
    /// Drives the real services (no fakes): cursor positioning on every monitor, engine clicks
    /// into a window of our own, pixel sampling, global hotkeys and the recorder hooks.
    /// It moves the mouse for a couple of seconds and only ever clicks inside its own window.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var s = _services!;
        var (homeX, homeY) = s.Input.GetCursor();
        int failures = 0;
        void Report(bool ok, string message)
        {
            if (!ok) failures++;
            Log.Info($"SELFTEST {(ok ? "PASS" : "FAIL")}: {message}");
        }

        try
        {
            await Task.Delay(500);

            // 1. SendInput absolute positioning: does the cursor land on the exact pixel, on every monitor?
            var random = new Random(1);
            int exact = 0, total = 0;
            string firstMiss = "";
            foreach (var m in s.Screen.GetMonitors())
            {
                var points = new List<(int X, int Y)> { (m.Left, m.Top), (m.Right - 1, m.Bottom - 1), (m.Left, m.Bottom - 1), (m.Right - 1, m.Top) };
                for (int i = 0; i < 60; i++) points.Add((random.Next(m.Left, m.Right), random.Next(m.Top, m.Bottom)));
                foreach (var (x, y) in points)
                {
                    s.Input.MoveTo(x, y);
                    await Task.Delay(6);
                    var got = s.Input.GetCursor();
                    total++;
                    if (got == (x, y)) exact++;
                    else if (firstMiss.Length == 0) firstMiss = $" (first miss: wanted {x},{y} got {got.X},{got.Y})";
                }
            }
            Report(exact == total, $"cursor positioning exact on {exact}/{total} points across {s.Screen.GetMonitors().Length} monitor(s){firstMiss}"
                + (exact == total ? "" : ". Touching the mouse during this step causes misses; rerun hands-off before trusting a failure."));

            // 2. Real clicks from the engine into our own window (normally refused, so allow it for the test).
            s.Engine.AllowOwnWindowClicks = true;
            int received = 0;
            var target = new Window
            {
                Width = 320, Height = 220, Topmost = true, Title = "Pizza Hero Clicker self-test",
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x7B, 0xC9, 0x50)) },
            };
            target.PreviewMouseLeftButtonDown += (_, _) => received++;
            target.Show();
            target.Activate();
            await Task.Delay(400);
            var centre = target.PointToScreen(new Point(150, 90)); // physical pixels
            int tx = (int)centre.X, ty = (int)centre.Y;

            // 3. Pixel sampling reads the colour we just drew at that screen position.
            var pixel = s.Screen.GetPixel(tx, ty);
            Report(pixel is { } p && p.Matches(new PixelColor(0x7B, 0xC9, 0x50), 2), $"pixel at ({tx}, {ty}) read as {pixel?.ToHex() ?? "nothing"}, expected #7BC950");

            // How long does one real SendInput click take on this machine? Everything installed that
            // hooks the mouse (overlays, macro tools, some drivers) adds to this.
            s.Input.MoveTo(tx, ty);
            await Task.Delay(50);
            var cost = new double[300];
            for (int i = 0; i < cost.Length; i++)
            {
                long a = TimingEngine.Now;
                s.Input.Click(ClickButton.Left);
                cost[i] = TimingEngine.TicksToMs(TimingEngine.Now - a);
            }
            Array.Sort(cost);
            Log.Info($"SELFTEST INFO: one SendInput click costs median {cost[150]:0.000} ms, p95 {cost[285]:0.000} ms, max {cost[^1]:0.000} ms");
            await Task.Delay(800);
            received = 0;

            foreach (var (intervalMs, clicks, plain) in new[] { (2, 250, false), (1, 1000, false) })
            {
                var profile = new Profile { IntervalMs = intervalMs, StartDelayMs = 0, CornerStop = false };
                profile.Repeat.Mode = RepeatMode.Count;
                profile.Repeat.Count = clicks;
                if (!plain) profile.Actions.Add(new ClickAction { X = tx, Y = ty }); // plain = click at the cursor

                var finished = new TaskCompletionSource<Engine.StopReason>();
                void OnStopped(Engine.StopReason reason, string? error) => finished.TrySetResult(reason);
                s.Engine.Stopped += OnStopped;
                received = 0;
                var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                long started = TimingEngine.Now;
                s.Engine.Start(profile);
                var stopReason = await finished.Task;
                double elapsed = TimingEngine.TicksToMs(TimingEngine.Now - started);
                double cpuMs = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                s.Engine.Stopped -= OnStopped;
                await Task.Delay(1500); // let WPF drain its input queue
                double expected = (clicks - 1) * intervalMs;
                string mode = plain ? "at the cursor" : "at a position";
                Report(received == clicks && stopReason == Engine.StopReason.Completed,
                    $"{intervalMs} ms {mode}: sent {clicks} real clicks, the window received {received} ({stopReason})");
                Report(Math.Abs(elapsed - expected) < expected * 0.03 + 10,
                    $"{intervalMs} ms {mode}: took {elapsed:0.0} ms (ideal {expected}), process CPU {cpuMs / elapsed:P0} of one core");
            }
            // Area watch, for real: two red squares must get LEFT clicks and a blue one a RIGHT click.
            var red = Color.FromRgb(0xE8, 0x43, 0x2E);
            var blue = Color.FromRgb(0x2E, 0x6B, 0xE8);
            int leftOnRed = 0, rightOnBlue = 0, wrongButton = 0;
            var field = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x7B, 0xC9, 0x50)) };
            void AddSquare(Color colour, double x, double y, bool wantsLeft)
            {
                var square = new Border { Width = 36, Height = 36, Background = new SolidColorBrush(colour) };
                Canvas.SetLeft(square, x);
                Canvas.SetTop(square, y);
                square.MouseLeftButtonDown += (_, _) =>
                {
                    if (wantsLeft) { leftOnRed++; square.Visibility = Visibility.Collapsed; } else wrongButton++;
                };
                square.MouseRightButtonDown += (_, _) =>
                {
                    if (!wantsLeft) { rightOnBlue++; square.Visibility = Visibility.Collapsed; } else wrongButton++;
                };
                field.Children.Add(square);
            }
            AddSquare(red, 30, 20, true);
            AddSquare(blue, 150, 70, false);
            AddSquare(red, 230, 120, true);
            target.Content = field;
            await Task.Delay(400);

            var fieldOrigin = field.PointToScreen(new Point(0, 0));
            double fieldScale = VisualTreeHelper.GetDpi(field).DpiScaleX;
            var watch = new AreaWatchAction
            {
                X = (int)fieldOrigin.X, Y = (int)fieldOrigin.Y,
                Width = (int)(field.ActualWidth * fieldScale), Height = (int)(field.ActualHeight * fieldScale),
                TimeoutMs = 1200, OnTimeout = PixelTimeoutBehavior.Stop, PollMs = 20,
                Rules =
                {
                    new AreaRule { Color = "#E8432E", Tolerance = 20, Button = ClickButton.Left },
                    new AreaRule { Color = "#2E6BE8", Tolerance = 20, Button = ClickButton.Right },
                },
            };
            var watchProfile = new Profile { IntervalMs = 80, StartDelayMs = 0, CornerStop = false };
            watchProfile.Actions.Add(watch);
            var watchDone = new TaskCompletionSource<Engine.StopReason>();
            void OnWatchStopped(Engine.StopReason reason, string? error) => watchDone.TrySetResult(reason);
            s.Engine.Stopped += OnWatchStopped;
            s.Engine.Start(watchProfile);
            var watchReason = await watchDone.Task;
            s.Engine.Stopped -= OnWatchStopped;
            await Task.Delay(300);
            Report(leftOnRed == 2 && rightOnBlue == 1 && wrongButton == 0 && watchReason == Engine.StopReason.PixelTimeout,
                $"area watch: left-clicked {leftOnRed}/2 red squares, right-clicked {rightOnBlue}/1 blue square, wrong-button clicks {wrongButton} (then {watchReason})");
            // Area watch by SHAPE: squares and circles of the SAME colour. Squares want a left click,
            // circles a right click. The pictures are snipped from the real screen, as a user would.
            int leftOnSquare = 0, rightOnCircle = 0, wrongShape = 0;
            var shapes = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)) };
            var grey = new SolidColorBrush(Color.FromRgb(0xB9, 0xB4, 0xA6));
            void AddShape(bool square, double x, double y)
            {
                System.Windows.Shapes.Shape shape = square
                    ? new System.Windows.Shapes.Rectangle { Width = 34, Height = 34, Fill = grey }
                    : new System.Windows.Shapes.Ellipse { Width = 34, Height = 34, Fill = grey };
                Canvas.SetLeft(shape, x);
                Canvas.SetTop(shape, y);
                shape.MouseLeftButtonDown += (_, _) =>
                {
                    if (square) { leftOnSquare++; shape.Visibility = Visibility.Collapsed; } else wrongShape++;
                };
                shape.MouseRightButtonDown += (_, _) =>
                {
                    if (!square) { rightOnCircle++; shape.Visibility = Visibility.Collapsed; } else wrongShape++;
                };
                shapes.Children.Add(shape);
            }
            AddShape(true, 30, 20);
            AddShape(false, 140, 30);
            AddShape(true, 220, 100);
            AddShape(false, 60, 110);
            target.Content = shapes;
            await Task.Delay(400);

            var shapesOrigin = shapes.PointToScreen(new Point(0, 0));
            double shapesScale = VisualTreeHelper.GetDpi(shapes).DpiScaleX;
            string SnipPicture(double x, double y)
            {
                // A snug box with a few pixels of background, like a hand-drawn snip.
                int size = (int)(46 * shapesScale);
                int[] snipped = [];
                s.Screen.TryCapture((int)(shapesOrigin.X + (x - 6) * shapesScale), (int)(shapesOrigin.Y + (y - 6) * shapesScale), size, size, ref snipped);
                return TemplateImage.Encode(snipped, size, size);
            }
            var shapeWatch = new AreaWatchAction
            {
                X = (int)shapesOrigin.X, Y = (int)shapesOrigin.Y,
                Width = (int)(shapes.ActualWidth * shapesScale), Height = (int)(shapes.ActualHeight * shapesScale),
                TimeoutMs = 1200, OnTimeout = PixelTimeoutBehavior.Stop, PollMs = 20,
                PictureMatch = PictureMatch.Exact, // same colours, different outlines: the exact matcher's job
                Rules =
                {
                    new AreaRule { Image = SnipPicture(30, 20), MatchPercent = 85, Button = ClickButton.Left },
                    new AreaRule { Image = SnipPicture(140, 30), MatchPercent = 85, Button = ClickButton.Right },
                },
            };
            var shapeProfile = new Profile { IntervalMs = 80, StartDelayMs = 0, CornerStop = false };
            shapeProfile.Actions.Add(shapeWatch);
            var shapeDone = new TaskCompletionSource<Engine.StopReason>();
            void OnShapeStopped(Engine.StopReason reason, string? error) => shapeDone.TrySetResult(reason);
            s.Engine.Stopped += OnShapeStopped;
            s.Engine.Start(shapeProfile);
            var shapeReason = await shapeDone.Task;
            s.Engine.Stopped -= OnShapeStopped;
            await Task.Delay(300);
            Report(leftOnSquare == 2 && rightOnCircle == 2 && wrongShape == 0 && shapeReason == Engine.StopReason.PixelTimeout,
                $"area watch by shape: left-clicked {leftOnSquare}/2 squares, right-clicked {rightOnCircle}/2 circles of the same colour, wrong-button clicks {wrongShape} (then {shapeReason})");

            // Area watch by APPEARANCE: "rocks" (mottled orange, or white) want a left click and a
            // "satellite" (grey body with black panels) wants a right click. The pictures are snipped
            // from upright samples; the targets on screen are ROTATED and differently sized, which
            // is what defeats exact matching in a real game.
            int leftOnRock = 0, rightOnSatellite = 0, wrongKind = 0;
            var space = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x34, 0x4C)) };
            FrameworkElement MakeTarget(string kind, double scale, double angle, double x, double y)
            {
                var art = new Canvas { Width = 60, Height = 40 };
                void Part(Color colour, double px, double py, double w, double h, bool round = false)
                {
                    System.Windows.Shapes.Shape part = round ? new System.Windows.Shapes.Ellipse() : new System.Windows.Shapes.Rectangle();
                    part.Width = w; part.Height = h; part.Fill = new SolidColorBrush(colour);
                    Canvas.SetLeft(part, px); Canvas.SetTop(part, py);
                    art.Children.Add(part);
                }
                switch (kind)
                {
                    case "orange":
                        Part(Color.FromRgb(0x4A, 0x30, 0x28), 8, 2, 44, 36, true);
                        Part(Color.FromRgb(0xE0, 0x7A, 0x2A), 16, 20, 9, 7, true);
                        Part(Color.FromRgb(0xE0, 0x7A, 0x2A), 34, 12, 8, 6, true);
                        Part(Color.FromRgb(0x80, 0x78, 0x78), 14, 6, 14, 6, true);
                        break;
                    case "white":
                        Part(Color.FromRgb(0xE4, 0xE4, 0xE4), 8, 2, 44, 36, true);
                        Part(Color.FromRgb(0xBC, 0xBC, 0xBC), 18, 10, 14, 10, true);
                        break;
                    default: // satellite
                        Part(Color.FromRgb(0x08, 0x08, 0x08), 0, 14, 22, 12);
                        Part(Color.FromRgb(0x08, 0x08, 0x08), 38, 14, 22, 12);
                        Part(Color.FromRgb(0xB8, 0xB8, 0xB8), 22, 8, 16, 24);
                        break;
                }
                art.RenderTransformOrigin = new Point(0.5, 0.5);
                art.RenderTransform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new RotateTransform(angle) } };
                // A transparent hit pad so a click anywhere on the target counts, whatever its rotation.
                var pad = new Border { Width = 60, Height = 40, Background = Brushes.Transparent, Child = art };
                Canvas.SetLeft(pad, x); Canvas.SetTop(pad, y);
                bool wantsLeft = kind != "satellite";
                pad.MouseLeftButtonDown += (_, _) =>
                {
                    if (wantsLeft) { leftOnRock++; pad.Visibility = Visibility.Collapsed; } else wrongKind++;
                };
                pad.MouseRightButtonDown += (_, _) =>
                {
                    if (!wantsLeft) { rightOnSatellite++; pad.Visibility = Visibility.Collapsed; } else wrongKind++;
                };
                space.Children.Add(pad);
                return pad;
            }

            // First show upright samples and snip a picture of each, with a little surround, as a user would.
            var sampleOrange = MakeTarget("orange", 1, 0, 20, 20);
            var sampleWhite = MakeTarget("white", 1, 0, 110, 20);
            var sampleSatellite = MakeTarget("satellite", 1, 0, 200, 20);
            target.Content = space;
            await Task.Delay(400);
            var spaceOrigin = space.PointToScreen(new Point(0, 0));
            double spaceScale = VisualTreeHelper.GetDpi(space).DpiScaleX;
            string SnipPart(double x, double y)
            {
                int w = (int)(72 * spaceScale), h = (int)(52 * spaceScale);
                int[] snipped = [];
                s.Screen.TryCapture((int)(spaceOrigin.X + (x - 6) * spaceScale), (int)(spaceOrigin.Y + (y - 6) * spaceScale), w, h, ref snipped);
                return TemplateImage.Encode(snipped, w, h);
            }
            string orangePicture = SnipPart(20, 20), whitePicture = SnipPart(110, 20), satellitePicture = SnipPart(200, 20);

            // Then replace them with rotated, resized targets.
            space.Children.Clear();
            MakeTarget("orange", 1.2, 35, 30, 30);
            MakeTarget("satellite", 0.9, 70, 150, 20);
            MakeTarget("white", 0.8, 120, 230, 60);
            MakeTarget("satellite", 1.15, -40, 60, 110);
            MakeTarget("orange", 0.85, 200, 190, 120);
            await Task.Delay(400);

            var spaceWatch = new AreaWatchAction
            {
                X = (int)spaceOrigin.X, Y = (int)spaceOrigin.Y,
                Width = (int)(space.ActualWidth * spaceScale), Height = (int)(space.ActualHeight * spaceScale),
                TimeoutMs = 1200, OnTimeout = PixelTimeoutBehavior.Stop, PollMs = 20, Priority = ScanPriority.Center,
                Rules =
                {
                    new AreaRule { Image = satellitePicture, Button = ClickButton.Right },
                    new AreaRule { Image = orangePicture, Button = ClickButton.Left },
                    new AreaRule { Image = whitePicture, Button = ClickButton.Left },
                },
            };
            var spaceProfile = new Profile { IntervalMs = 80, StartDelayMs = 0, CornerStop = false };
            spaceProfile.Actions.Add(spaceWatch);
            var spaceDone = new TaskCompletionSource<Engine.StopReason>();
            void OnSpaceStopped(Engine.StopReason reason, string? error) => spaceDone.TrySetResult(reason);
            s.Engine.Stopped += OnSpaceStopped;
            s.Engine.AreaDebugFolder = AppPaths.Debug; // also exercise the troubleshooting recorder
            s.Engine.Start(spaceProfile);
            var spaceReason = await spaceDone.Task;
            s.Engine.Stopped -= OnSpaceStopped;
            s.Engine.AreaDebugFolder = null;
            await Task.Delay(600);
            string debugReport = File.Exists(Path.Combine(AppPaths.Debug, "report.txt")) ? File.ReadAllText(Path.Combine(AppPaths.Debug, "report.txt")) : "";
            Report(File.Exists(Path.Combine(AppPaths.Debug, "frame-01.png")) && debugReport.Contains("most like picture"),
                "troubleshooting recorder saved a frame and a report of the objects it saw");
            Report(leftOnRock == 3 && rightOnSatellite == 2 && wrongKind == 0 && spaceReason == Engine.StopReason.PixelTimeout,
                $"area watch by appearance (rotated and resized targets): left-clicked {leftOnRock}/3 rocks, right-clicked {rightOnSatellite}/2 satellites, wrong-button clicks {wrongKind} (then {spaceReason})");
            target.Close();

            // 4. Global hotkeys: the start hotkey must start a run, the emergency hotkey must stop it.
            // Skipped when any hotkey is owned by someone else (typically another running copy of this
            // app): pressing it would start a run over there instead of here.
            var hotkeys = _viewModel!.Profile.Hotkeys;
            if (_viewModel.HotkeyWarning.Length > 0)
            {
                Log.Info("SELFTEST SKIP: hotkey check not run because another program (probably another copy of this app) holds the hotkeys.");
            }
            else
            {
                s.Input.KeyComboDown(hotkeys.Toggle);
                s.Input.KeyComboUp(hotkeys.Toggle);
                await Task.Delay(300);
                bool startedByHotkey = s.Engine.IsActive;
                s.Input.KeyComboDown(hotkeys.Stop);
                s.Input.KeyComboUp(hotkeys.Stop);
                await Task.Delay(300);
                Report(startedByHotkey && !s.Engine.IsActive, $"hotkey {hotkeys.Toggle} started a run ({startedByHotkey}) and {hotkeys.Stop} stopped it ({!s.Engine.IsActive})");
            }

            // 5. Recorder: hooks install, and synthetic input is NOT recorded (only physical input should be).
            bool hooked = s.Recorder.Start(recordMouseMoves: true);
            s.Input.MoveTo(tx + 5, ty + 5);
            s.Input.KeyComboDown(new KeyCombo(0x87)); // F24: harmless
            s.Input.KeyComboUp(new KeyCombo(0x87));
            await Task.Delay(300);
            int recorded = s.Recorder.Stop().Count;
            Report(hooked, "recorder hooks installed");
            Report(recorded <= 3, $"recorder ignored synthetic input (captured {recorded} events during the test; a few are possible if you touched the mouse)");
        }
        catch (Exception ex)
        {
            failures++;
            Log.Error("SELFTEST crashed", ex);
        }
        finally
        {
            s.Engine.Stop();
            s.Input.MoveTo(homeX, homeY);
            Log.Info($"SELFTEST finished with {failures} failure(s).");
            s.Engine.AllowOwnWindowClicks = false;
            Quit(failures == 0 ? 0 : 1);
        }
    }

    // ------------------------------------------------------------------ developer snapshot mode

    private async Task RunSnapshotAsync(MainWindow window, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Task Settle() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

            // A freshly loaded profile must not look edited just because the UI bound to it.
            for (int i = 0; i < window.TabHost.Items.Count; i++)
            {
                window.TabHost.SelectedIndex = i;
                await Settle();
            }
            Log.Info($"Unsaved-changes flag after loading and visiting every tab: {_viewModel!.IsDirty} (expected False)");
            // Switching game through the GAME list must open that game's first profile, and back again.
            if (_viewModel.GameNames.Count > 1)
            {
                string startGame = _viewModel.SelectedGame!, startProfile = _viewModel.SelectedProfileName!;
                string otherGame = _viewModel.GameNames.First(g => g != startGame);
                _viewModel.SelectedGame = otherGame;
                await Settle();
                Log.Info($"Picked game '{otherGame}': now on '{_viewModel.Profile.Game}' / '{_viewModel.Profile.Name}', profiles listed: [{string.Join(", ", _viewModel.ProfileNames)}], unsaved: {_viewModel.IsDirty}");
                _viewModel.SelectedGame = startGame;
                await Settle();
                _viewModel.SelectedProfileName = startProfile;
                await Settle();
                Log.Info($"Back to '{startGame}' / '{startProfile}': now on '{_viewModel.Profile.Game}' / '{_viewModel.Profile.Name}', games listed: [{string.Join(", ", _viewModel.GameNames)}], profiles listed: [{string.Join(", ", _viewModel.ProfileNames)}]");
            }

            AddSampleActions(_viewModel.Profile);
            Log.Info($"Unsaved-changes flag after an edit: {_viewModel.IsDirty} (expected True)");

            for (int i = 0; i < window.TabHost.Items.Count; i++)
            {
                window.TabHost.SelectedIndex = i;
                await Settle();
                string name = ((TabItem)window.TabHost.Items[i]).Header?.ToString() ?? i.ToString();
                SaveSnapshot(window, Path.Combine(dir, $"{i + 1}-{name.ToLowerInvariant().Replace(' ', '-').Replace("&", "and")}.png"));
            }

            // The game picker, showing its "new game" box.
            var picker = new GamePickerWindow("Move to another game", "Which game should \"Meteorite mini game\" be filed under?",
                ["Galaxy Idle Clicker", "Cookie Game"], "Galaxy Idle Clicker", MainViewModel.NoGame, "MOVE")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            picker.Show();
            await Settle();
            SaveSnapshot(picker, Path.Combine(dir, "game-picker.png"));
            ((ComboBox)((StackPanel)picker.Content).Children[2]).SelectedIndex = 3; // "+ New game…"
            await Settle();
            SaveSnapshot(picker, Path.Combine(dir, "game-picker-new.png"));
            picker.Close();

            // The import review, for a profile that tries to run a command and for a harmless one.
            var hostile = new Profile();
            hostile.Actions.Add(new ClickAction { X = 400, Y = 300 });
            hostile.Actions.Add(new KeyPressAction { Keys = { KeyCombo.Parse("Win+R") } });
            var typed = new KeyPressAction();
            foreach (string key in new[] { "C", "M", "D", "Enter" }) typed.Keys.Add(KeyCombo.Parse(key));
            hostile.Actions.Add(typed);
            var harmless = new Profile();
            harmless.Window.Enabled = true;
            harmless.Window.Title = "Galaxy Idle Clicker";
            harmless.Window.ProcessName = "GalaxyIdleClicker";
            harmless.Actions.Add(new ClickAction { X = 400, Y = 300 });
            harmless.Actions.Add(new WaitAction());
            foreach (var (sample, file) in new[] { (hostile, "review-risky.png"), (harmless, "review-clean.png") })
            {
                var reviewWindow = new ProfileReviewWindow("Import profile", "Free gems farm", ProfileInspector.Inspect(sample), importing: true)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                reviewWindow.Show();
                await Settle();
                SaveSnapshot(reviewWindow, Path.Combine(dir, file));
                reviewWindow.Close();
            }

            // Row numbers must stay 1, 2, 3... after rows are moved (they once did not).
            window.TabHost.SelectedIndex = 1;
            _viewModel.MoveAction(_viewModel.Profile.Actions.Count - 1, 0);
            _viewModel.MoveAction(3, 1);
            await Settle();
            SaveSnapshot(window, Path.Combine(dir, "actions-after-moves.png"));

            foreach (var action in _viewModel.Profile.Actions.Where(a => a is KeyPressAction or PixelClickAction or AreaWatchAction))
            {
                var editor = new ActionEditorWindow(new ActionEditorViewModel(action.Clone(), "Edit action",
                    () => Task.FromResult<PickResult?>(null), () => Task.FromResult<RegionPick?>(null), _ => Task.FromResult<string?>(null), _ => Task.CompletedTask,
                    () => Task.FromResult<(RegionPick, string)?>(null)))
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                editor.Show();
                await Settle();
                SaveSnapshot(editor, Path.Combine(dir, $"editor-{action.TypeName.ToLowerInvariant().Replace(' ', '-')}.png"));
                editor.Close();
            }
            await CheckOverlaysAsync(window);
            Quit(0);
        }
        catch (Exception ex)
        {
            Log.Error("Snapshot failed", ex);
            Quit(1);
        }
    }

    /// <summary>Turns both overlays on and logs whether a marker's crosshair really covers its target pixel on each monitor.</summary>
    private async Task CheckOverlaysAsync(MainWindow window)
    {
        var profile = _viewModel!.Profile;
        var monitors = _services!.Screen.GetMonitors();
        profile.Actions.Clear();
        foreach (var m in monitors)
        {
            Log.Info($"Monitor {m.Left},{m.Top} {m.Width}x{m.Height}");
            profile.Actions.Add(new ClickAction { X = m.Left + m.Width / 3, Y = m.Top + m.Height / 3 });
            profile.Actions.Add(new ClickAction { X = m.Right - 40, Y = m.Bottom - 80 });
        }
        window.WindowState = WindowState.Minimized; // get our own window out of the way of the sampled pixels
        profile.Overlay.ShowStatus = true;
        profile.Overlay.ShowPositions = true;
        await Task.Delay(1200);

        foreach (var action in profile.Actions.OfType<ClickAction>())
            Log.Info($"Marker at ({action.X}, {action.Y}): screen pixel there is {_services.Screen.GetPixel(action.X, action.Y)?.ToHex() ?? "off-screen"} (crosshair colour is #FFC533)");
        foreach (Window w in Windows)
        {
            if (w is MainWindow) continue;
            var handle = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            Native.NativeMethods.GetWindowRect(handle, out var r);
            Log.Info($"{w.GetType().Name}: {r.Left},{r.Top} {r.Width}x{r.Height}, scale {VisualTreeHelper.GetDpi(w).DpiScaleX}");
        }
        profile.Overlay.ShowStatus = false;
        profile.Overlay.ShowPositions = false;
        await Task.Delay(300);
    }

    private static void AddSampleActions(Profile profile)
    {
        profile.Actions.Add(new ClickAction { X = 812, Y = 440, Label = "Collect button" });
        profile.Actions.Add(new ClickAction { X = -1240, Y = 300, Button = ClickButton.Right, Kind = ClickKind.Double, HoldMs = 40, IntervalMs = 250, IntervalMaxMs = 400 });
        profile.Actions.Add(new KeyPressAction { Keys = { KeyCombo.Parse("Ctrl+C"), KeyCombo.Parse("Ctrl+V"), KeyCombo.Parse("Enter") }, HoldMs = 20 });
        profile.Actions.Add(new WaitAction { Ms = 1500, Enabled = false });
        profile.Actions.Add(new ScrollAction { Direction = ScrollDirection.Down, Amount = 3 });
        profile.Actions.Add(new Models.DragAction { X1 = 100, Y1 = 200, X2 = 640, Y2 = 480, DurationMs = 350 });
        profile.Actions.Add(new PixelClickAction { X = 955, Y = 610, Color = "#E8432E", Tolerance = 12, TimeoutMs = 8000 });
        profile.Actions.Add(new AreaWatchAction
        {
            X = 600, Y = 200, Width = 900, Height = 700,
            Rules =
            {
                new AreaRule { Image = SamplePicture(false), Button = ClickButton.Left },
                new AreaRule { Image = SamplePicture(true), Button = ClickButton.Right },
                new AreaRule { Color = "#3FA9F5", Button = ClickButton.Right },
            },
        });
    }

    /// <summary>A small drawn picture (a rock or a satellite-like cross) for the editor snapshot.</summary>
    private static string SamplePicture(bool cross)
    {
        const int size = 40;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int dx = x - size / 2, dy = y - size / 2;
            bool on = cross ? Math.Abs(dx) < 4 || Math.Abs(dy) < 4 || dx * dx + dy * dy < 49 : dx * dx + dy * dy < 196;
            pixels[y * size + x] = on ? 0xB9B4A6 : 0x05060A;
        }
        return TemplateImage.Encode(pixels, size, size);
    }

    private static void SaveSnapshot(Window window, string path)
    {
        var root = window.Content is ScrollViewer { Content: FrameworkElement inner } ? inner : (FrameworkElement)window.Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        var m = root.Margin;
        double w = root.ActualWidth + m.Left + m.Right, h = root.ActualHeight + m.Top + m.Bottom;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, new Rect(m.Left, m.Top, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
