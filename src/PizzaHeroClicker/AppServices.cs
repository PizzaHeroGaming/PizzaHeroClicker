using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker;

/// <summary>The app's long-lived services, created once in <see cref="App"/> and handed to the view model.</summary>
public sealed class AppServices : IDisposable
{
    public InputService Input { get; } = new();
    public ScreenService Screen { get; } = new();
    public WindowService Windows { get; } = new();
    public TimingEngine Timing { get; } = new();
    public HotkeyService Hotkeys { get; } = new();
    public ProfileService Profiles { get; } = new(AppPaths.Profiles, AppPaths.SettingsFile);
    public Models.AppSettings Settings { get; }
    public TrayService Tray { get; } = new();
    public RecorderService Recorder { get; } = new();
    public UpdateService Updates { get; } = new();
    public OverlayService Overlays { get; }
    public ClickEngine Engine { get; }
    public Views.DialogService DialogHost { get; }
    public IDialogService Dialogs => DialogHost;

    public AppServices()
    {
        Settings = Profiles.LoadSettings();
        Overlays = new OverlayService(Screen);
        Engine = new ClickEngine(Input, Screen, Windows, Timing);
        DialogHost = new Views.DialogService(Screen);
    }

    public void Dispose()
    {
        Engine.Dispose();   // stops the run and joins the engine thread first
        Recorder.Dispose();
        Updates.Dispose();
        Overlays.Dispose();
        Tray.Dispose();
        Hotkeys.Dispose();
        Timing.Dispose();
        Screen.Dispose();
    }
}
