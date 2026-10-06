using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class ProfileInspectorTests
{
    private static Profile GameProfile(params ActionBase[] actions)
    {
        var profile = new Profile();
        profile.Window.Enabled = true;
        profile.Window.Title = "Some Game";
        profile.Window.ProcessName = "game";
        foreach (var action in actions) profile.Actions.Add(action);
        return profile;
    }

    private static KeyPressAction Keys(params string[] combos)
    {
        var action = new KeyPressAction();
        foreach (string combo in combos) action.Keys.Add(KeyCombo.Parse(combo));
        return action;
    }

    private static bool Has(ProfileReview review, ReviewLevel level, string fragment) =>
        review.Notes.Any(n => n.Level == level && n.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AClickOnlyProfileLockedToItsGameRaisesNoConcern()
    {
        var review = ProfileInspector.Inspect(GameProfile(new ClickAction { X = 10, Y = 10 }, new WaitAction()));
        Assert.Equal(ReviewLevel.Info, review.Worst);
        Assert.Equal(0, review.KeyActions);
        Assert.True(Has(review, ReviewLevel.Info, "Some Game"));
        Assert.True(Has(review, ReviewLevel.Info, "2 actions"));
    }

    [Fact]
    public void OpeningTheRunBoxAndTypingACommandIsFlaggedAsARisk()
    {
        var review = ProfileInspector.Inspect(GameProfile(Keys("Win+R"), new WaitAction(), Keys("C", "M", "D", "Enter")));
        Assert.Equal(ReviewLevel.Danger, review.Worst);
        Assert.True(Has(review, ReviewLevel.Danger, "Windows key"));
        Assert.True(Has(review, ReviewLevel.Danger, "\"cmd\""));
        Assert.Equal(ReviewLevel.Danger, review.Notes[0].Level); // the serious things come first
        Assert.Equal(2, review.KeyActions);
    }

    [Fact]
    public void TypedTextIsReadAcrossSeparateActionsAndWhenSwitchedOff()
    {
        // One letter per action, all switched off: still a profile that can type "powershell".
        var actions = "powershell".Select(c => (ActionBase)new KeyPressAction { Keys = { KeyCombo.Parse(c.ToString()) }, Enabled = false }).ToArray();
        var review = ProfileInspector.Inspect(GameProfile(actions));
        Assert.True(Has(review, ReviewLevel.Danger, "\"powershell\""));
    }

    [Theory]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Escape")]
    public void WindowSwitchingShortcutsAreFlagged(string combo) =>
        Assert.True(Has(ProfileInspector.Inspect(GameProfile(Keys(combo))), ReviewLevel.Danger, "switches or closes windows"));

    [Fact]
    public void OrdinaryGameKeysAreListedButNotCalledDangerous()
    {
        var review = ProfileInspector.Inspect(GameProfile(Keys("1"), new ClickAction(), Keys("Space"), Keys("E"), Keys("F5")));
        Assert.Equal(ReviewLevel.Caution, review.Worst);
        Assert.True(Has(review, ReviewLevel.Caution, "In order: 1, Space, E, F5"));
        Assert.DoesNotContain(review.Notes, n => n.Text.Contains("Types the text"));
    }

    [Fact]
    public void AProfileNotLockedToAWindowIsCalledOut()
    {
        var profile = GameProfile(new ClickAction { X = 5, Y = 5 });
        profile.Window.Enabled = false;
        Assert.True(Has(ProfileInspector.Inspect(profile), ReviewLevel.Caution, "Not locked to a game window"));
    }

    [Fact]
    public void KeyPressesCanBeSwitchedOffWithoutLosingThem()
    {
        var profile = GameProfile(Keys("Win+R"), new ClickAction(), Keys("A"));
        Assert.Equal(2, ProfileInspector.TurnKeyPressesOff(profile));
        Assert.All(profile.Actions.OfType<KeyPressAction>(), a => Assert.False(a.Enabled));
        Assert.True(profile.Actions.OfType<ClickAction>().Single().Enabled);
        Assert.Equal(3, profile.Actions.Count);
    }

    [Fact]
    public void AnImportedProfileCannotBringItsOwnControls()
    {
        var imported = GameProfile(new ClickAction());
        imported.Hotkeys.Toggle = KeyCombo.Parse("Enter");   // an everyday key that would start it
        imported.Hotkeys.Stop = default;                      // and no way to stop it
        imported.QuickSwitchHotkey = KeyCombo.Parse("Space");
        imported.CornerStop = false;

        var mine = new HotkeySettings { Toggle = KeyCombo.Parse("F10"), Mode = HotkeyMode.Hold };
        ProfileInspector.KeepControlsWithTheUser(imported, mine);

        Assert.Equal(KeyCombo.Parse("F10"), imported.Hotkeys.Toggle);
        Assert.Equal(mine.Stop, imported.Hotkeys.Stop);
        Assert.Equal(HotkeyMode.Hold, imported.Hotkeys.Mode);
        Assert.True(imported.QuickSwitchHotkey.IsEmpty);
        Assert.True(imported.CornerStop);
    }
}
