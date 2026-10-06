using System.IO;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Identifies a profile: the game it belongs to (empty = not filed under a game) and its name.
/// On disk that is profiles\&lt;Game&gt;\&lt;Name&gt;.json, or profiles\&lt;Name&gt;.json without a game.
/// </summary>
public readonly record struct ProfileRef(string Game, string Name)
{
    /// <summary>A single string form, "Game/Name" or just "Name", used in settings and hotkey ids.</summary>
    public string Key => Game.Length == 0 ? Name : Game + "/" + Name;

    public string Display => Game.Length == 0 ? Name : $"{Game} / {Name}";

    /// <summary>Same profile, ignoring letter case (as the file system does).</summary>
    public bool Is(ProfileRef other) =>
        string.Equals(Game, other.Game, StringComparison.OrdinalIgnoreCase) && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public static ProfileRef FromKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return new ProfileRef("", "");
        int slash = key.IndexOf('/');
        return slash < 0 ? new ProfileRef("", key) : new ProfileRef(key[..slash], key[(slash + 1)..]);
    }

    /// <summary>A bare name means a profile that is not filed under any game.</summary>
    public static implicit operator ProfileRef(string name) => new("", name);
}

/// <summary>Compares profile references ignoring letter case.</summary>
public sealed class ProfileRefComparer : IEqualityComparer<ProfileRef>
{
    public static readonly ProfileRefComparer Instance = new();
    public bool Equals(ProfileRef a, ProfileRef b) => a.Is(b);
    public int GetHashCode(ProfileRef r) => StringComparer.OrdinalIgnoreCase.GetHashCode(r.Key);
}

/// <summary>
/// Loads and saves profiles (one JSON file each, grouped into a folder per game) and the app
/// settings file. All methods are synchronous and are called from the UI thread; the files
/// are small, so this never blocks noticeably.
/// </summary>
public sealed class ProfileService
{
    public const string Extension = ".json";
    private const int MaxNameLength = 60;

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private readonly string _settingsFile;
    private Dictionary<ProfileRef, KeyCombo>? _quickSwitchCache;

    public string Folder { get; }

    public ProfileService(string profilesFolder, string settingsFile)
    {
        Folder = profilesFolder;
        _settingsFile = settingsFile;
        Directory.CreateDirectory(Folder);
    }

    private string FolderOf(string game) => game.Length == 0 ? Folder : Path.Combine(Folder, game);
    private string PathOf(ProfileRef profile) => Path.Combine(FolderOf(profile.Game), profile.Name + Extension);

    // ------------------------------------------------------------------ names

    /// <summary>Checks that a profile or game name can be used as a file / folder name. Returns null when it is fine.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The name can't be empty.";
        if (name != name.Trim()) return "The name can't start or end with a space.";
        if (name.Length > MaxNameLength) return $"The name can't be longer than {MaxNameLength} characters.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The name can't contain any of  \\ / : * ? \" < > |";
        if (name.EndsWith('.')) return "The name can't end with a dot.";
        if (ReservedNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) return "That name is reserved by Windows.";
        return null;
    }

    /// <summary>Turns arbitrary text into a usable profile name.</summary>
    public static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength].Trim();
        return NameProblem(cleaned) is null ? cleaned : "Profile";
    }

    public bool Exists(ProfileRef profile) => File.Exists(PathOf(profile));

    /// <summary>Returns <paramref name="baseName"/>, or "baseName 2", "baseName 3", ... if that game already has it.</summary>
    public string UniqueName(string game, string baseName)
    {
        if (!Exists(new ProfileRef(game, baseName))) return baseName;
        for (int i = 2; ; i++)
        {
            string suffix = " " + i;
            string stem = baseName.Length + suffix.Length > MaxNameLength ? baseName[..(MaxNameLength - suffix.Length)].TrimEnd() : baseName;
            if (!Exists(new ProfileRef(game, stem + suffix))) return stem + suffix;
        }
    }

    public string UniqueName(string baseName) => UniqueName("", baseName);

    /// <summary>Every profile, ungrouped ones first, then by game and name.</summary>
    public IReadOnlyList<ProfileRef> List()
    {
        var result = new List<ProfileRef>();
        void AddFrom(string folder, string game)
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*" + Extension))
            {
                string? name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(name)) result.Add(new ProfileRef(game, name));
            }
        }

        AddFrom(Folder, "");
        foreach (string folder in Directory.EnumerateDirectories(Folder))
            AddFrom(folder, Path.GetFileName(folder));

        return result
            .OrderBy(p => p.Game.Length == 0 ? 0 : 1)
            .ThenBy(p => p.Game, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The games that have at least one profile. "" (no game) comes first when it has any.</summary>
    public IReadOnlyList<string> Games() => List().Select(p => p.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ------------------------------------------------------------------ profiles

    /// <summary>Loads a profile. Throws if the file is missing or is not a valid profile.</summary>
    public Profile Load(ProfileRef profile) => Read(PathOf(profile), profile);

    private static Profile Read(string path, ProfileRef identity)
    {
        var profile = ProfileJson.Deserialize<Profile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The file does not contain a profile.");
        if (profile.SchemaVersion > Profile.CurrentSchemaVersion)
            throw new InvalidDataException("This profile was made by a newer version of Pizza Hero Clicker.");
        profile.Normalize();
        // Where the file sits is what names it: the folder is its game, the file name its name.
        profile.Name = identity.Name;
        profile.Game = identity.Game;
        return profile;
    }

    public static ProfileRef RefOf(Profile profile) => new(profile.Game, profile.Name);

    public void Save(Profile profile)
    {
        profile.ModifiedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteAtomic(PathOf(RefOf(profile)), ProfileJson.Serialize(profile));
        _quickSwitchCache = null;
    }

    public void Rename(ProfileRef profile, string newName)
    {
        // Two-step so a case-only rename ("farm" to "Farm") works on a case-insensitive file system.
        string temp = PathOf(profile) + ".renaming";
        File.Move(PathOf(profile), temp, overwrite: true);
        File.Move(temp, PathOf(profile with { Name = newName }), overwrite: false);
        _quickSwitchCache = null;
    }

    /// <summary>Moves a profile to another game (created if needed; "" = no game). Throws if the name is taken there.</summary>
    public void Move(ProfileRef profile, string newGame)
    {
        if (string.Equals(profile.Game, newGame, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(FolderOf(newGame));
        File.Move(PathOf(profile), PathOf(profile with { Game = newGame }), overwrite: false);
        RemoveFolderIfEmpty(profile.Game);
        _quickSwitchCache = null;
    }

    /// <summary>
    /// Renames a game, taking all of its profiles along. Renaming "" files every ungrouped
    /// profile under the new game; renaming onto an existing game merges into it.
    /// Throws, having changed nothing, if any profile name is already taken in the destination.
    /// </summary>
    public void RenameGame(string oldGame, string newGame)
    {
        if (oldGame == newGame) return;
        var members = List().Where(p => string.Equals(p.Game, oldGame, StringComparison.OrdinalIgnoreCase)).ToList();

        if (string.Equals(oldGame, newGame, StringComparison.OrdinalIgnoreCase))
        {
            // Only the letter case changes: rename the folder itself, via a temporary name.
            string temp = FolderOf(oldGame) + ".renaming";
            Directory.Move(FolderOf(oldGame), temp);
            Directory.Move(temp, FolderOf(newGame));
            _quickSwitchCache = null;
            return;
        }

        var clash = members.FirstOrDefault(p => Exists(p with { Game = newGame }));
        if (clash.Name is { Length: > 0 })
            throw new IOException($"\"{newGame}\" already has a profile called \"{clash.Name}\".");

        Directory.CreateDirectory(FolderOf(newGame));
        foreach (var member in members) File.Move(PathOf(member), PathOf(member with { Game = newGame }), overwrite: false);
        RemoveFolderIfEmpty(oldGame);
        _quickSwitchCache = null;
    }

    public void Duplicate(ProfileRef source, string newName)
    {
        var copy = Load(source);
        copy.Name = newName;
        copy.CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        copy.QuickSwitchHotkey = default; // two profiles must not share a quick-switch key
        Save(copy);
    }

    public void Delete(ProfileRef profile)
    {
        File.Delete(PathOf(profile));
        RemoveFolderIfEmpty(profile.Game);
        _quickSwitchCache = null;
    }

    /// <summary>A game exists only while it has profiles, so its folder goes when the last one leaves.</summary>
    private void RemoveFolderIfEmpty(string game)
    {
        if (game.Length == 0) return;
        try
        {
            string folder = FolderOf(game);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the empty game folder '{game}': {ex.Message}");
        }
    }

    /// <summary>Imports a profile file into a game under a free name. Throws if the file is invalid.</summary>
    public ProfileRef Import(string sourcePath, string game = "")
    {
        var profile = Read(sourcePath, new ProfileRef(game, "import"));
        string name = UniqueName(game, Sanitize(Path.GetFileNameWithoutExtension(sourcePath)));
        profile.Name = name;
        if (QuickSwitchHotkeys().Values.Contains(profile.QuickSwitchHotkey)) profile.QuickSwitchHotkey = default;
        Directory.CreateDirectory(FolderOf(game));
        Save(profile);
        return new ProfileRef(game, name);
    }

    public void Export(ProfileRef profile, string destinationPath) => File.Copy(PathOf(profile), destinationPath, overwrite: true);

    /// <summary>Writes a profile as it currently is in memory (including unsaved edits) to any file.</summary>
    public void Export(Profile profile, string destinationPath) => WriteAtomic(destinationPath, ProfileJson.Serialize(profile));

    /// <summary>Quick-switch hotkey of every profile that has one (cached until a profile changes on disk).</summary>
    public IReadOnlyDictionary<ProfileRef, KeyCombo> QuickSwitchHotkeys()
    {
        if (_quickSwitchCache is not null) return _quickSwitchCache;
        var map = new Dictionary<ProfileRef, KeyCombo>(ProfileRefComparer.Instance);
        foreach (var profile in List())
        {
            try
            {
                var combo = Load(profile).QuickSwitchHotkey;
                if (!combo.IsEmpty) map[profile] = combo;
            }
            catch (Exception ex)
            {
                Log.Warn($"Skipping unreadable profile '{profile.Display}': {ex.Message}");
            }
        }
        return _quickSwitchCache = map;
    }

    // ------------------------------------------------------------------ settings

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var settings = ProfileJson.Deserialize<AppSettings>(File.ReadAllText(_settingsFile));
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings; using defaults", ex);
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            WriteAtomic(_settingsFile, ProfileJson.Serialize(settings));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    /// <summary>Write to a temp file and swap it in, so a crash mid-write can't leave a half-written profile.</summary>
    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
