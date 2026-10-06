using System.IO;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _service = new ProfileService(Path.Combine(_root, "profiles"), Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Profile Sample(string name, string game = "", string hotkey = "Ctrl+Alt+1")
    {
        var profile = new Profile { Name = name, Game = game, IntervalMs = 42, QuickSwitchHotkey = KeyCombo.Parse(hotkey) };
        profile.Actions.Add(new ClickAction { X = -5, Y = 9 });
        profile.Actions.Add(new KeyPressAction { Keys = { KeyCombo.Parse("Shift+A") } });
        return profile;
    }

    private string[] Listed() => _service.List().Select(p => p.Display).ToArray();

    [Fact]
    public void SaveLoadRenameDuplicateDelete()
    {
        _service.Save(Sample("Farm"));
        Assert.Equal(["Farm"], Listed());

        var loaded = _service.Load("Farm");
        Assert.Equal(42, loaded.IntervalMs);
        Assert.Equal(2, loaded.Actions.Count);
        Assert.True(loaded.ModifiedAtMs > 0);

        _service.Rename("Farm", "farm");            // case-only rename
        Assert.Equal(["farm"], Listed());
        _service.Rename("farm", "Mining");
        Assert.Equal("Mining", _service.Load("Mining").Name);

        _service.Duplicate("Mining", "Mining copy");
        Assert.Equal(["Mining", "Mining copy"], Listed());
        Assert.True(_service.Load("Mining copy").QuickSwitchHotkey.IsEmpty); // the copy must not steal the hotkey
        var hotkeys = _service.QuickSwitchHotkeys();
        Assert.Equal(KeyCombo.Parse("Ctrl+Alt+1"), Assert.Single(hotkeys).Value);
        Assert.Equal("Mining", hotkeys.Keys.Single().Name);

        _service.Delete("Mining");
        Assert.Equal(["Mining copy"], Listed());
        Assert.Empty(_service.QuickSwitchHotkeys());
    }

    [Fact]
    public void ProfilesAreGroupedByGameAndNamesOnlyNeedToBeUniqueWithinOne()
    {
        _service.Save(Sample("Old ungrouped"));
        _service.Save(Sample("Main", "Galaxy Idle Clicker", "Ctrl+Alt+2"));
        _service.Save(Sample("Minerals", "Galaxy Idle Clicker", "Ctrl+Alt+3"));
        _service.Save(Sample("Main", "Cookie Game", "Ctrl+Alt+4"));     // same name, different game

        Assert.Equal(["Old ungrouped", "Cookie Game / Main", "Galaxy Idle Clicker / Main", "Galaxy Idle Clicker / Minerals"], Listed());
        Assert.Equal(["", "Cookie Game", "Galaxy Idle Clicker"], _service.Games());
        Assert.True(File.Exists(Path.Combine(_root, "profiles", "Galaxy Idle Clicker", "Minerals.json")));

        // The game comes from the folder, not from inside the file.
        var loaded = _service.Load(new ProfileRef("Cookie Game", "Main"));
        Assert.Equal(("Cookie Game", "Main"), (loaded.Game, loaded.Name));
        Assert.DoesNotContain("\"game\"", File.ReadAllText(Path.Combine(_root, "profiles", "Cookie Game", "Main.json")));

        Assert.Equal("Main 2", _service.UniqueName("Cookie Game", "Main"));
        Assert.Equal("Main", _service.UniqueName("Brand New Game", "Main"));
        Assert.Equal(4, _service.QuickSwitchHotkeys().Count);
    }

    [Fact]
    public void MovingAndRenamingGames()
    {
        _service.Save(Sample("Click Shot"));
        _service.Save(Sample("Minerals"));
        _service.Save(Sample("Main", "Other"));

        // File every ungrouped profile under a game in one go.
        _service.RenameGame("", "Galaxy Idle Clicker");
        Assert.Equal(["Galaxy Idle Clicker / Click Shot", "Galaxy Idle Clicker / Minerals", "Other / Main"], Listed());

        // Move one profile across; the game it leaves disappears once empty.
        _service.Move(new ProfileRef("Other", "Main"), "Galaxy Idle Clicker");
        Assert.Equal(["Galaxy Idle Clicker"], _service.Games());
        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", "Other")));

        // Rename the game, including a change of letter case only.
        _service.RenameGame("Galaxy Idle Clicker", "GIC");
        _service.RenameGame("GIC", "Gic");
        Assert.Equal(["Gic / Click Shot", "Gic / Main", "Gic / Minerals"], Listed());

        // Back out to "no game".
        _service.Move(new ProfileRef("Gic", "Main"), "");
        Assert.Equal(["Main", "Gic / Click Shot", "Gic / Minerals"], Listed());

        // A clash is refused and nothing is moved.
        _service.Save(Sample("Minerals"));
        Assert.Throws<IOException>(() => _service.RenameGame("", "Gic"));
        Assert.ThrowsAny<IOException>(() => _service.Move("Minerals", "Gic"));
        Assert.Equal(["Main", "Minerals", "Gic / Click Shot", "Gic / Minerals"], Listed());
    }

    [Fact]
    public void ProfileRefKeysRoundTrip()
    {
        Assert.Equal(new ProfileRef("Game", "Name"), ProfileRef.FromKey("Game/Name"));
        Assert.Equal(new ProfileRef("", "Name"), ProfileRef.FromKey("Name"));     // settings written before games existed
        Assert.Equal("Game/Name", new ProfileRef("Game", "Name").Key);
        Assert.Equal("Name", new ProfileRef("", "Name").Key);
        Assert.True(new ProfileRef("game", "NAME").Is(new ProfileRef("Game", "name")));
    }

    [Fact]
    public void ImportPicksAFreeNameAndExportRoundTrips()
    {
        _service.Save(Sample("Boss"));
        string exported = Path.Combine(_root, "Boss.json");
        _service.Export("Boss", exported);

        var imported = _service.Import(exported);
        Assert.Equal(new ProfileRef("", "Boss 2"), imported);
        var copy = _service.Load(imported);
        Assert.Equal(2, copy.Actions.Count);
        Assert.True(copy.QuickSwitchHotkey.IsEmpty); // already taken by "Boss"

        // Importing into a game puts it in that game's folder under its original name.
        var intoGame = _service.Import(exported, "Raid Game");
        Assert.Equal(new ProfileRef("Raid Game", "Boss"), intoGame);
        Assert.Equal("Raid Game", _service.Load(intoGame).Game);
    }

    [Fact]
    public void BadFilesAreRejectedWithoutBeingImported()
    {
        string junk = Path.Combine(_root, "junk.json");
        File.WriteAllText(junk, "{ this is not json");
        Assert.ThrowsAny<Exception>(() => _service.Import(junk));

        string future = Path.Combine(_root, "future.json");
        File.WriteAllText(future, """{ "schemaVersion": 999 }""");
        Assert.Throws<InvalidDataException>(() => _service.Import(future));

        string unknown = Path.Combine(_root, "unknown.json");
        File.WriteAllText(unknown, """{ "actions": [ { "type": "teleport" } ] }""");
        Assert.ThrowsAny<Exception>(() => _service.Import(unknown));

        Assert.Empty(_service.List());
    }

    [Theory]
    [InlineData("Minecraft farm", true)]
    [InlineData("", false)]
    [InlineData(" padded", false)]
    [InlineData("a/b", false)]
    [InlineData("what?", false)]
    [InlineData("CON", false)]
    [InlineData("ends.", false)]
    public void NamesAreValidated(string name, bool ok) => Assert.Equal(ok, ProfileService.NameProblem(name) is null);

    [Fact]
    public void SanitizeAlwaysProducesAUsableName()
    {
        foreach (string raw in new[] { "a/b:c", "  spaced  ", "", "NUL", new string('x', 200), "dots..." })
            Assert.Null(ProfileService.NameProblem(ProfileService.Sanitize(raw)));
    }

    [Fact]
    public void TheReadmeExampleProfileIsValid()
    {
        // Walk up from the test output folder to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string readme = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
        int start = readme.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        string json = readme[start..readme.IndexOf("```", start, StringComparison.Ordinal)];

        string file = Path.Combine(_root, "Example.json");
        File.WriteAllText(file, json);
        var profile = _service.Load(_service.Import(file));

        Assert.Equal(8, profile.Actions.Count);
        Assert.Equal(8, profile.Actions.Select(a => a.GetType()).Distinct().Count()); // one of every action type shown
        Assert.Equal(KeyCombo.Parse("Ctrl+Shift+R"), profile.Hotkeys.Record);
        Assert.Equal(["Ctrl+C", "Enter"], ((KeyPressAction)profile.Actions[1]).Keys.Select(k => k.ToString()));
    }

    [Fact]
    public void SettingsRoundTripAndSurviveCorruption()
    {
        _service.SaveSettings(new AppSettings { LastProfile = "Boss", StartMinimized = true, MaxDelayMs = 750 });
        var settings = _service.LoadSettings();
        Assert.Equal("Boss", settings.LastProfile);
        Assert.True(settings.StartMinimized);
        Assert.Equal(750, settings.MaxDelayMs);

        File.WriteAllText(Path.Combine(_root, "settings.json"), "garbage");
        Assert.Equal("", _service.LoadSettings().LastProfile);
    }
}
