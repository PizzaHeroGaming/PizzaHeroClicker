using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

// Games and profiles (load, save, rename, move, duplicate, delete, import, export), app settings, portable mode.
public partial class MainViewModel
{
    private const string ProfileFilter = "Pizza Hero Clicker profile (*.json)|*.json|All files (*.*)|*.*";
    private const string HkProfilePrefix = "profile:";

    /// <summary>How profiles that are not filed under a game are labelled in the game list.</summary>
    public const string NoGame = "(No game)";

    private bool _switching;
    private ProfileRef? _loaded; // the profile that was last read from disk

    /// <summary>Games that have profiles, as shown in the GAME list (<see cref="NoGame"/> for ungrouped ones).</summary>
    public ObservableCollection<string> GameNames { get; } = new();

    /// <summary>Names of the profiles in the selected game.</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();

    [ObservableProperty] private string? _selectedGame;
    [ObservableProperty] private string? _selectedProfileName;

    /// <summary>True when the profile has edits that have not been written to disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isDirty;

    public string SaveButtonText => IsDirty ? "SAVE ●" : "SAVED";

    public AppSettings Settings => _s.Settings;

    public string DataFolder => AppPaths.Root;
    public string PortableStatus => AppPaths.IsPortable
        ? "Portable mode is ON: profiles, settings and logs are stored next to the exe."
        : "Portable mode is OFF: data is stored in your AppData folder.";
    public string PortableButtonText => AppPaths.IsPortable ? "TURN PORTABLE MODE OFF" : "TURN PORTABLE MODE ON";

    private ProfileRef CurrentRef => ProfileService.RefOf(Profile);
    private static string GameLabel(string game) => game.Length == 0 ? NoGame : game;
    private static string GameFromLabel(string? label) => label is null || label == NoGame ? "" : label;

    // ------------------------------------------------------------------ startup / shutdown

    private void InitializeProfiles()
    {
        Settings.PropertyChanged += OnSettingsChanged;
        _s.Timing.Precise = Settings.PreciseTiming;

        var all = _s.Profiles.List();
        if (all.Count == 0)
        {
            TrySave(new Profile { Name = "Default" });
            all = _s.Profiles.List();
        }

        // Last-used profile first, then anything else that loads.
        var last = ProfileRef.FromKey(Settings.LastProfile);
        foreach (var candidate in all.OrderByDescending(p => p.Is(last)))
        {
            if (LoadProfile(candidate)) return;
        }

        // Nothing on disk is usable: carry on with a fresh profile.
        Profile = new Profile { Name = _s.Profiles.UniqueName("", "Default") };
        _loaded = CurrentRef;
        TrySave(Profile);
        IsDirty = false;
        RefreshLists();
        ApplyHotkeys();
    }

    partial void OnShutdown() => _s.Profiles.SaveSettings(Settings);

    /// <summary>App settings (not profiles) are small and global, so they are written as soon as they change.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.PreciseTiming)) _s.Timing.Precise = Settings.PreciseTiming;
        _s.Profiles.SaveSettings(Settings);
    }

    // ------------------------------------------------------------------ saving

    partial void AfterProfileEdited(object? sender, PropertyChangedEventArgs e)
    {
        OnWindowSettingEdited(sender, e);
        UpdateOverlays(force: true);
        IsDirty = true; // nothing is written until the user presses Save
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (!TrySave(Profile)) return;
        IsDirty = false;
        Footer = $"Saved \"{Profile.Name}\".";
    }

    /// <summary>Throws away unsaved edits and reloads the profile from disk.</summary>
    [RelayCommand]
    private void RevertProfile()
    {
        if (!IsDirty)
        {
            Footer = "Nothing to revert: there are no unsaved changes.";
            return;
        }
        if (!_s.Dialogs.Confirm("Revert profile", $"Discard the unsaved changes to \"{Profile.Name}\" and go back to the saved version?", "Revert")) return;
        var current = CurrentRef;
        IsDirty = false;
        _loaded = null; // force a re-read
        if (LoadProfile(current)) Footer = $"Reverted \"{current.Name}\" to the saved version.";
    }

    /// <summary>
    /// Call before anything that replaces the loaded profile. If there are unsaved edits the
    /// user chooses to save them, drop them, or cancel. Returns false if the caller must not proceed.
    /// </summary>
    private bool ConfirmLeaveProfile()
    {
        if (!IsDirty) return true;
        int choice = _s.Dialogs.Choose("Unsaved changes", $"Save the changes to \"{Profile.Name}\"?", "Save", "Don't save", "Cancel");
        switch (choice)
        {
            case 0:
                if (!TrySave(Profile)) return false;
                IsDirty = false;
                return true;
            case 1:
                IsDirty = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Asked by the main window before it closes. False cancels the close.</summary>
    public bool ConfirmClose() => ConfirmLeaveProfile();

    /// <summary>Drops the unsaved flag without prompting (developer modes that exit on their own).</summary>
    public void DiscardChanges() => IsDirty = false;

    private bool TrySave(Profile profile)
    {
        try
        {
            _s.Profiles.Save(profile);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save profile '{profile.Name}'", ex);
            Footer = $"Could not save the profile: {ex.Message}";
            _s.Dialogs.Inform("Save profile", "The profile could not be saved.\n\n" + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ the GAME and PROFILE lists

    /// <summary>Rebuilds both lists from disk and points them at the loaded profile.</summary>
    private void RefreshLists()
    {
        _switching = true;
        try
        {
            var all = _s.Profiles.List();
            string game = Profile.Game;

            GameNames.Clear();
            foreach (string g in all.Select(p => p.Game).Distinct(StringComparer.OrdinalIgnoreCase)) GameNames.Add(GameLabel(g));
            // The loaded profile's game is always listed, even before its first save.
            if (!GameNames.Contains(GameLabel(game), StringComparer.OrdinalIgnoreCase)) GameNames.Add(GameLabel(game));

            ProfileNames.Clear();
            foreach (var p in all.Where(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase))) ProfileNames.Add(p.Name);

            SelectedGame = GameNames.FirstOrDefault(g => string.Equals(g, GameLabel(game), StringComparison.OrdinalIgnoreCase));
            SelectedProfileName = ProfileNames.FirstOrDefault(n => string.Equals(n, Profile.Name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _switching = false;
        }
    }

    partial void OnSelectedGameChanged(string? value)
    {
        if (_switching || value is null) return;
        // Deferred so the combo box finishes its own selection change before we possibly revert it.
        _ui.BeginInvoke(() =>
        {
            // Picking a game opens its first profile.
            string game = GameFromLabel(value);
            var first = _s.Profiles.List().FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase));
            if (first.Name is not { Length: > 0 } || !LoadProfile(first)) RefreshLists();
        });
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        if (_switching || value is null) return;
        _ui.BeginInvoke(() =>
        {
            if (!LoadProfile(new ProfileRef(GameFromLabel(SelectedGame), value))) RefreshLists();
        });
    }

    /// <summary>
    /// Makes a profile the active one. Offers to save unsaved edits first (returns false if the
    /// user cancels) and stops any run.
    /// </summary>
    private bool LoadProfile(ProfileRef target)
    {
        if (_loaded is { } loadedRef && loadedRef.Is(target))
        {
            RefreshLists();
            return true;
        }
        if (!ConfirmLeaveProfile()) return false;

        _s.Engine.Stop();

        Profile loaded;
        try
        {
            loaded = _s.Profiles.Load(target);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load profile '{target.Display}'", ex);
            Footer = $"Could not load \"{target.Display}\": {ex.Message}";
            return false;
        }

        Profile = loaded;
        _loaded = target;
        SelectedAction = null;
        RefreshLists();
        Settings.LastProfile = target.Key;
        ApplyHotkeys();
        Revalidate();
        AfterProfileLoaded();
        IsDirty = false;
        Footer = $"Loaded profile \"{target.Display}\".";
        return true;
    }

    /// <summary>Hook for the other partial files (overlays).</summary>
    partial void AfterProfileLoaded();

    private void SwitchToProfileByHotkey(string key)
    {
        var target = ProfileRef.FromKey(key);
        if (_s.Profiles.Exists(target)) LoadProfile(target);
    }

    // ------------------------------------------------------------------ profile commands

    /// <summary>Asks for a name and checks it can be a file name. Returns null if cancelled or invalid.</summary>
    private string? AskForName(string title, string message, string suggestion)
    {
        string? name = _s.Dialogs.Prompt(title, message, suggestion);
        if (name is null) return null;
        if (ProfileService.NameProblem(name) is { } problem)
        {
            _s.Dialogs.Inform(title, problem);
            return null;
        }
        return name;
    }

    /// <summary>Asks for a profile name that is free in <paramref name="game"/>.</summary>
    private string? AskForProfileName(string title, string message, string suggestion, string game, string? allowExisting = null)
    {
        string? name = AskForName(title, message, suggestion);
        if (name is null) return null;
        bool sameAsAllowed = allowExisting is not null && string.Equals(name, allowExisting, StringComparison.OrdinalIgnoreCase);
        if (_s.Profiles.Exists(new ProfileRef(game, name)) && !sameAsAllowed)
        {
            _s.Dialogs.Inform(title, $"{GameLabel(game)} already has a profile called \"{name}\".");
            return null;
        }
        return name;
    }

    private void CreateProfile(string game, string name)
    {
        if (!ConfirmLeaveProfile() || !TrySave(new Profile { Game = game, Name = name })) return;
        LoadProfile(new ProfileRef(game, name));
    }

    /// <summary>A new profile in the game that is currently selected.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        string game = Profile.Game;
        string? name = AskForProfileName("New profile", $"Name for the new profile in {GameLabel(game)}:", _s.Profiles.UniqueName(game, "New profile"), game);
        if (name is not null) CreateProfile(game, name);
    }

    /// <summary>A new game, with its first profile.</summary>
    [RelayCommand]
    private void NewGame()
    {
        string? game = AskForName("New game", "Name of the game:", "");
        if (game is null) return;
        string? name = AskForProfileName("New game", $"Name for the first profile in {game}:", _s.Profiles.UniqueName(game, "Main"), game);
        if (name is not null) CreateProfile(game, name);
    }

    /// <summary>Renames the profile's file. Unsaved edits stay unsaved, under the new name.</summary>
    [RelayCommand]
    private void RenameProfile()
    {
        var old = CurrentRef;
        string? name = AskForProfileName("Rename profile", $"New name for \"{old.Name}\":", old.Name, old.Game, allowExisting: old.Name);
        if (name is null || name == old.Name) return;

        _s.Engine.Stop();
        if (!TryFileChange("Rename profile", "The profile could not be renamed", () => _s.Profiles.Rename(old, name))) return;
        AdoptIdentity(old with { Name = name });
        Footer = $"Renamed to \"{name}\".";
    }

    /// <summary>Files the profile under a different game (or none).</summary>
    [RelayCommand]
    private void MoveProfile()
    {
        var old = CurrentRef;
        string? game = _s.Dialogs.ChooseGame("Move to another game", $"Which game should \"{old.Name}\" be filed under?",
            _s.Profiles.Games(), old.Game, NoGame, "MOVE");
        if (game is null || string.Equals(game, old.Game, StringComparison.Ordinal)) return;
        if (game == NoGame) game = "";
        if (game.Length > 0 && ProfileService.NameProblem(game) is { } problem)
        {
            _s.Dialogs.Inform("Move to another game", problem);
            return;
        }
        // Reuse the existing spelling of a game if only the letter case differs.
        game = _s.Profiles.Games().FirstOrDefault(g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase)) ?? game;
        if (_s.Profiles.Exists(old with { Game = game }))
        {
            _s.Dialogs.Inform("Move to another game", $"{GameLabel(game)} already has a profile called \"{old.Name}\". Rename this one first.");
            return;
        }

        _s.Engine.Stop();
        if (!TryFileChange("Move to another game", "The profile could not be moved", () => _s.Profiles.Move(old, game))) return;
        AdoptIdentity(old with { Game = game });
        Footer = $"Moved \"{old.Name}\" to {GameLabel(game)}.";
    }

    /// <summary>
    /// Renames the current game, taking all its profiles along. On "(No game)" this files every
    /// ungrouped profile under a game in one go.
    /// </summary>
    [RelayCommand]
    private void RenameGame()
    {
        var old = CurrentRef;
        string title = old.Game.Length == 0 ? "Put these profiles in a game" : "Rename game";
        string message = old.Game.Length == 0
            ? "Name of the game that all the profiles under (No game) belong to:"
            : $"New name for the game \"{old.Game}\" (all its profiles come along):";
        string? game = AskForName(title, message, old.Game);
        if (game is null || game == old.Game || game == NoGame) return;

        _s.Engine.Stop();
        if (!TryFileChange(title, "The game could not be renamed", () => _s.Profiles.RenameGame(old.Game, game))) return;
        // Renaming onto an existing game merges into it and keeps that game's spelling.
        string actual = _s.Profiles.Games().FirstOrDefault(g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase)) ?? game;
        AdoptIdentity(old with { Game = actual });
        Footer = old.Game.Length == 0 ? $"Filed the ungrouped profiles under \"{actual}\"." : $"Game renamed to \"{actual}\".";
    }

    /// <summary>Runs a file operation, reporting a failure to the user. Returns false if it failed.</summary>
    private bool TryFileChange(string title, string failure, Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(failure, ex);
            _s.Dialogs.Inform(title, $"{failure}: {ex.Message}");
            return false;
        }
    }

    /// <summary>After the loaded profile's file was renamed or moved: give the in-memory profile its new identity.</summary>
    private void AdoptIdentity(ProfileRef identity)
    {
        _loading = true; // a new name or game is not an unsaved edit
        Profile.Name = identity.Name;
        Profile.Game = identity.Game;
        _loading = false;
        _loaded = identity;
        Settings.LastProfile = identity.Key;
        RefreshLists();
        ApplyHotkeys();
    }

    /// <summary>Copies the saved profile under a new name in the same game and switches to the copy.</summary>
    [RelayCommand]
    private void DuplicateProfile()
    {
        var source = CurrentRef;
        string? name = AskForProfileName("Duplicate profile", "Name for the copy:", _s.Profiles.UniqueName(source.Game, source.Name + " copy"), source.Game);
        if (name is null || !ConfirmLeaveProfile()) return;
        if (!TryFileChange("Duplicate profile", "The profile could not be duplicated", () => _s.Profiles.Duplicate(source, name))) return;
        LoadProfile(source with { Name = name });
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var doomed = CurrentRef;
        if (!_s.Dialogs.Confirm("Delete profile", $"Delete the profile \"{doomed.Display}\"? This can't be undone.", "Delete")) return;

        _s.Engine.Stop();
        if (!TryFileChange("Delete profile", "The profile could not be deleted", () => _s.Profiles.Delete(doomed))) return;
        _loaded = null;
        IsDirty = false; // its unsaved edits went with it

        var all = _s.Profiles.List();
        if (all.Count == 0)
        {
            TrySave(new Profile { Name = "Default" });
            all = _s.Profiles.List();
        }
        // Prefer staying in the same game.
        foreach (var candidate in all.OrderByDescending(p => string.Equals(p.Game, doomed.Game, StringComparison.OrdinalIgnoreCase)))
        {
            if (LoadProfile(candidate)) break;
        }
        Footer = $"Deleted \"{doomed.Display}\".";
    }

    /// <summary>Imports a profile file into the game that is currently selected.</summary>
    [RelayCommand]
    private void ImportProfile()
    {
        string? path = _s.Dialogs.OpenFile("Import profile", ProfileFilter);
        if (path is null || !ConfirmLeaveProfile()) return;
        try
        {
            var imported = _s.Profiles.Import(path, Profile.Game);
            LoadProfile(imported);
            Footer = $"Imported \"{imported.Display}\".";
        }
        catch (Exception ex)
        {
            Log.Error($"Import of '{path}' failed", ex);
            _s.Dialogs.Inform("Import profile", "That file could not be imported. It doesn't look like a valid profile.\n\n" + ex.Message);
        }
    }

    /// <summary>Exports the profile as it is right now, including unsaved edits.</summary>
    [RelayCommand]
    private void ExportProfile()
    {
        string? path = _s.Dialogs.SaveFile("Export profile", ProfileFilter, Profile.Name + ProfileService.Extension);
        if (path is null) return;
        if (TryFileChange("Export profile", "The profile could not be exported", () => _s.Profiles.Export(Profile, path)))
            Footer = $"Exported to {path}.";
    }

    // ------------------------------------------------------------------ folders / portable mode

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.Root);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.Logs);

    [RelayCommand]
    private void OpenDebugFolder() => OpenFolder(AppPaths.Debug);

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open folder '{folder}'", ex);
            Footer = "Could not open the folder: " + ex.Message;
        }
    }

    /// <summary>
    /// Switches between AppData and portable storage. The saved profiles and settings are copied
    /// to the new location and the flag file is created or removed; the change applies on the next start.
    /// </summary>
    [RelayCommand]
    private void TogglePortable()
    {
        bool toPortable = !AppPaths.IsPortable;
        string target = toPortable ? AppPaths.ExeDir : AppPaths.AppDataRoot;
        string message = toPortable
            ? $"Copy your saved profiles and settings next to the exe and store everything there from now on?\n\n{target}"
            : $"Copy your saved profiles and settings to AppData and store everything there from now on?\n\n{target}";
        if (IsDirty) message += "\n\nThis profile has unsaved changes. Save it first if you want them included.";
        if (!_s.Dialogs.Confirm("Portable mode", message, "Switch")) return;

        _s.Profiles.SaveSettings(Settings);
        try
        {
            // Every profile, keeping the per-game folders.
            string source = _s.Profiles.Folder, targetProfiles = Path.Combine(target, "profiles");
            foreach (string file in Directory.EnumerateFiles(source, "*" + ProfileService.Extension, SearchOption.AllDirectories))
            {
                string destination = Path.Combine(targetProfiles, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            if (File.Exists(AppPaths.SettingsFile))
                File.Copy(AppPaths.SettingsFile, Path.Combine(target, "settings.json"), overwrite: true);

            if (toPortable) File.WriteAllText(AppPaths.PortableFlag, "Pizza Hero Clicker stores its data next to the exe while this file exists.");
            else File.Delete(AppPaths.PortableFlag);

            _s.Dialogs.Inform("Portable mode", "Done. Close and reopen Pizza Hero Clicker to finish switching.");
        }
        catch (Exception ex)
        {
            Log.Error("Switching portable mode failed", ex);
            _s.Dialogs.Inform("Portable mode", "Could not switch. The folder may be read-only.\n\n" + ex.Message);
        }
    }
}
