using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

// Checking for a newer version and, when the user says so, installing it.
public partial class MainViewModel
{
    /// <summary>Set by the app: closes this copy and runs the downloaded installer.</summary>
    public Action<string>? LaunchInstaller { get; set; }

    public string VersionText => $"version {UpdateService.CurrentVersion}";

    [ObservableProperty] private string _updateStatus = "Not checked yet.";
    [ObservableProperty] private string _updateNotes = "";
    [ObservableProperty] private string _aboutHeader = "ABOUT";
    [ObservableProperty] private string _installUpdateText = "UPDATE";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    private bool _updateBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    private UpdateInfo? _update;

    public bool UpdateAvailable => Update is not null;

    /// <summary>The quiet check at startup: says nothing unless there is something newer.</summary>
    public async Task CheckForUpdatesQuietlyAsync()
    {
        if (!Settings.CheckForUpdates) return;
        await CheckAsync(quiet: true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckForUpdates() => CheckAsync(quiet: false);
    private bool CanCheck() => !UpdateBusy;

    private async Task CheckAsync(bool quiet)
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        if (!quiet) UpdateStatus = "Checking...";
        try
        {
            var latest = await _s.Updates.GetLatestAsync();
            if (latest is not null && UpdateService.IsNewer(latest.Version, UpdateService.CurrentVersion))
            {
                Update = latest;
                UpdateNotes = latest.Notes;
                UpdateStatus = $"Version {latest.Version} is available. You have {UpdateService.CurrentVersion}.";
                // A portable copy is not something an installer should touch; a release without a
                // checkable installer is not something to run. Both get the download page instead.
                InstallUpdateText = AppPaths.IsPortable || latest.InstallerUrl is null ? "OPEN DOWNLOAD PAGE" : $"UPDATE TO {latest.Version}";
                AboutHeader = "ABOUT ●";
                if (quiet) Footer = $"Version {latest.Version} is available: see the About tab.";
            }
            else
            {
                Update = null;
                UpdateNotes = "";
                AboutHeader = "ABOUT";
                UpdateStatus = $"You have the latest version. Last checked {DateTime.Now:t}.";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            if (!quiet) UpdateStatus = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdate()
    {
        if (Update is not { } update) return;
        if (AppPaths.IsPortable || update.InstallerUrl is null || LaunchInstaller is null)
        {
            OpenWebPage(update.PageUrl);
            return;
        }
        if (_s.Engine.IsActive || IsRecording)
        {
            UpdateStatus = "Stop the run or recording first, then update.";
            return;
        }
        if (!ConfirmLeaveProfile()) return; // the app is about to close

        UpdateBusy = true;
        try
        {
            var progress = new Progress<double>(share => UpdateStatus = $"Downloading version {update.Version}... {share:P0}");
            string installer = await _s.Updates.DownloadInstallerAsync(update, progress);
            if (_s.Engine.IsActive)
            {
                UpdateStatus = "A run started while downloading. Stop it, then press update again.";
                return;
            }
            UpdateStatus = "Starting the installer...";
            Log.Info($"Updating from {UpdateService.CurrentVersion} to {update.Version} with {installer}");
            LaunchInstaller(installer);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateStatus = "The update could not be installed: " + ex.Message;
        }
        finally
        {
            UpdateBusy = false;
        }
    }
    private bool CanInstall() => !UpdateBusy && Update is not null;

    [RelayCommand]
    private void OpenReleasesPage() => OpenWebPage(Update?.PageUrl ?? UpdateService.ReleasesPage);

    private void OpenWebPage(string url)
    {
        try
        {
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = UpdateService.ReleasesPage;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
            UpdateStatus = "Could not open the web page: " + ex.Message;
        }
    }
}
