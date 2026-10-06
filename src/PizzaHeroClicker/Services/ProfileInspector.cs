using System.Text;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

public enum ReviewLevel
{
    /// <summary>Good to know.</summary>
    Info,
    /// <summary>Worth a look before running.</summary>
    Caution,
    /// <summary>Has no business in a game profile; do not run unless you know why it is there.</summary>
    Danger,
}

public sealed record ReviewNote(ReviewLevel Level, string Text);

/// <summary>What a profile would do, in plain words, for someone deciding whether to trust it.</summary>
public sealed class ProfileReview
{
    public List<ReviewNote> Notes { get; } = new();
    /// <summary>How many actions press keys (enabled or not).</summary>
    public int KeyActions { get; set; }
    public ReviewLevel Worst => Notes.Count == 0 ? ReviewLevel.Info : Notes.Max(n => n.Level);
}

/// <summary>
/// Reads a profile the way a careful person would before running someone else's: what it clicks,
/// which keys it presses, and anything that looks like it is doing more than playing a game.
/// A profile is a script of mouse and keyboard input, so one from a stranger deserves that look.
/// </summary>
public static class ProfileInspector
{
    private const int VkLWin = 0x5B, VkRWin = 0x5C, VkEnter = 0x0D, VkTab = 0x09, VkEscape = 0x1B, VkSpace = 0x20, VkF4 = 0x73;
    private const int MaxKeysListed = 40;

    /// <summary>Words that have no place in a game profile's typed text.</summary>
    private static readonly string[] SuspiciousText =
        ["cmd", "powershell", "pwsh", "http", "www", "exe", "curl", "wget", "mshta", "wscript", "cscript", "regedit", "reg add", "bitsadmin", "rundll", "shutdown", "format", "del "];

    public static ProfileReview Inspect(Profile profile)
    {
        var review = new ProfileReview();
        var notes = review.Notes;
        var actions = profile.Actions;

        // ---- keys: the part that can do real harm
        var keyActions = actions.OfType<KeyPressAction>().Where(a => a.Keys.Count > 0).ToList();
        var keys = keyActions.SelectMany(a => a.Keys).Where(k => !k.IsEmpty).ToList();
        review.KeyActions = keyActions.Count;

        var winKeys = keys.Where(k => k.Mods.HasFlag(KeyMods.Win) || k.Vk is VkLWin or VkRWin).Select(k => k.Display).Distinct().ToList();
        if (winKeys.Count > 0)
            notes.Add(new(ReviewLevel.Danger, $"Uses the Windows key ({string.Join(", ", winKeys)}). That can open the Run box, the Start menu or system shortcuts. A game profile almost never needs it."));

        var systemKeys = keys.Where(IsWindowShortcut).Select(k => k.Display).Distinct().ToList();
        if (systemKeys.Count > 0)
            notes.Add(new(ReviewLevel.Danger, $"Presses {string.Join(", ", systemKeys)}, which switches or closes windows. After that, its clicks and keys would land in a different program."));

        foreach (var (text, thenEnter) in TypedText(keys))
        {
            string lower = text.ToLowerInvariant();
            bool suspicious = thenEnter || SuspiciousText.Any(lower.Contains);
            notes.Add(new(suspicious ? ReviewLevel.Danger : ReviewLevel.Caution,
                $"Types the text \"{text}\"" + (thenEnter ? " and presses Enter" : "") + "."
                + (suspicious ? " Typing a line and entering it is how a profile would run a command. Check that this is something the game expects." : "")));
        }

        if (keys.Count > 0)
        {
            string listed = string.Join(", ", keys.Take(MaxKeysListed).Select(k => k.Display));
            if (keys.Count > MaxKeysListed) listed += $", and {keys.Count - MaxKeysListed} more";
            notes.Add(new(ReviewLevel.Caution, $"Presses keys. They go to whichever window is in front at the time. In order: {listed}."));
        }

        // ---- where its input can land
        if (profile.Window.Enabled && profile.Window.HasWindow && profile.Window.OnlyWhenFocused)
            notes.Add(new(ReviewLevel.Info, $"Only runs while this window is in front: {profile.Window.Description}."));
        else
            notes.Add(new(ReviewLevel.Caution, "Not locked to a game window: it clicks" + (keys.Count > 0 ? " and types" : "") + " on whatever is on screen when it runs. Have only the game in front."));

        bool positioned = actions.Any(a => a is ClickAction { UseCursor: false } or Models.DragAction or MoveAction or PixelActionBase or AreaWatchAction or ScrollAction { UseCursor: false });
        if (positioned && !(profile.Window.Enabled && profile.Window.RelativeCoordinates))
            notes.Add(new(ReviewLevel.Info, "Uses fixed screen positions from the author's screen. With a different resolution or window position it can click the wrong things, so try it first where a stray click does no harm."));

        // ---- what is in it
        notes.Add(new(ReviewLevel.Info, Contents(profile)));
        if (profile.Repeat.Mode == RepeatMode.Infinite)
            notes.Add(new(ReviewLevel.Info, "Repeats until you stop it."));

        // Most serious first; within a level, the order written above.
        var ordered = notes.OrderByDescending(n => n.Level).ToList();
        notes.Clear();
        notes.AddRange(ordered);
        return review;
    }

    /// <summary>Switches every key-pressing action off (they stay in the list to be looked at and turned back on). Returns how many.</summary>
    public static int TurnKeyPressesOff(Profile profile)
    {
        int count = 0;
        foreach (var action in profile.Actions.OfType<KeyPressAction>().Where(a => a.Enabled))
        {
            action.Enabled = false;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Applied to every imported profile, whatever the review says: the ways of starting and
    /// stopping a run stay the user's own, so a profile cannot bind an everyday key to "start",
    /// remove the stop key, or switch off the mouse-to-corner emergency stop.
    /// </summary>
    public static void KeepControlsWithTheUser(Profile imported, HotkeySettings mine)
    {
        imported.Hotkeys.Mode = mine.Mode;
        imported.Hotkeys.Toggle = mine.Toggle;
        imported.Hotkeys.Pause = mine.Pause;
        imported.Hotkeys.Capture = mine.Capture;
        imported.Hotkeys.Stop = mine.Stop;
        imported.Hotkeys.Overlay = mine.Overlay;
        imported.Hotkeys.Record = mine.Record;
        imported.QuickSwitchHotkey = default;
        imported.CornerStop = true;
    }

    private static bool IsWindowShortcut(KeyCombo k)
    {
        bool alt = k.Mods.HasFlag(KeyMods.Alt), ctrl = k.Mods.HasFlag(KeyMods.Ctrl);
        if (alt && (k.Vk == VkTab || k.Vk == VkF4 || k.Vk == VkEscape || k.Vk == VkSpace)) return true;
        return ctrl && k.Vk == VkEscape;
    }

    /// <summary>
    /// Runs of plain character keys, read back as the text they would type. Short runs (a few
    /// game keys such as W, A, S, D pressed in turn) are left alone unless Enter follows.
    /// </summary>
    private static IEnumerable<(string Text, bool ThenEnter)> TypedText(List<KeyCombo> keys)
    {
        var run = new StringBuilder();
        for (int i = 0; i <= keys.Count; i++)
        {
            char c = i < keys.Count ? CharOf(keys[i]) : '\0';
            if (c != '\0')
            {
                run.Append(c);
                continue;
            }
            bool enter = i < keys.Count && keys[i] is { Vk: VkEnter, Mods: KeyMods.None };
            string text = run.ToString().Trim();
            run.Clear();
            if (text.Length >= 4 || (enter && text.Length >= 2)) yield return (text, enter);
        }
    }

    /// <summary>The character a key types, or '\0' if it is not a plain character key.</summary>
    private static char CharOf(KeyCombo k)
    {
        if (k.Mods is not (KeyMods.None or KeyMods.Shift)) return '\0';
        return k.Vk switch
        {
            >= 0x41 and <= 0x5A => (char)(k.Vk + 32),         // letters
            >= 0x30 and <= 0x39 => (char)k.Vk,                 // top-row digits
            >= 0x60 and <= 0x69 => (char)('0' + k.Vk - 0x60),  // number pad
            VkSpace => ' ',
            0xBA => k.Mods == KeyMods.Shift ? ':' : ';',
            0xBB => '=',
            0xBC => ',',
            0xBD => '-',
            0xBE or 0x6E => '.',
            0xBF or 0x6F => '/',
            0xDC => '\\',
            0xDE => '"',
            _ => '\0',
        };
    }

    private static string Contents(Profile profile)
    {
        var actions = profile.Actions;
        if (actions.Count == 0) return "No actions: it just clicks where the mouse is, at the profile's interval.";
        var parts = actions.GroupBy(a => a.TypeName).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} × {g.Key.ToLowerInvariant()}");
        int off = actions.Count(a => !a.Enabled);
        return $"{actions.Count} action{(actions.Count == 1 ? "" : "s")}: {string.Join(", ", parts)}" + (off > 0 ? $" ({off} switched off)" : "") + ".";
    }
}
