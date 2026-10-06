using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>A point picked on screen (physical virtual-screen pixels) and the colour found there.</summary>
public sealed record PickResult(int X, int Y, PixelColor Color);

/// <summary>A rectangle dragged out on screen, in physical virtual-screen pixels.</summary>
public sealed record RegionPick(int X, int Y, int Width, int Height);

/// <summary>Everything the view models need from the UI layer, so they never create windows themselves.</summary>
public interface IDialogService
{
    /// <summary>Opens the editor on <paramref name="action"/> (edited in place). Returns true if the user saved.</summary>
    bool EditAction(ActionBase action, string title, Func<Task<PickResult?>> pickPoint, Func<Task<RegionPick?>> pickRegion);

    /// <summary>Lets the user drag a rectangle on any monitor. Null if cancelled.</summary>
    Task<RegionPick?> PickRegionAsync();

    /// <summary>Lets the user click any point on any monitor. Null if cancelled.</summary>
    Task<PickResult?> PickPointAsync();

    /// <summary>If a point pick is in progress, completes it at the current cursor position.</summary>
    bool TryAcceptPickAtCursor();

    bool Confirm(string title, string message, string confirmText = "OK");

    /// <summary>Shows a message with custom buttons. Returns the index of the button pressed, or -1 if dismissed.</summary>
    int Choose(string title, string message, params string[] buttons);

    /// <summary>Asks for a line of text. Null if cancelled.</summary>
    string? Prompt(string title, string message, string initialValue = "");

    void Inform(string title, string message);

    /// <summary>
    /// Shows what a profile from someone else would do. Returns 0 to import it as it is, 1 to
    /// import it with its key presses switched off, -1 to cancel. With <paramref name="importing"/>
    /// false it only informs (one Close button, returns -1).
    /// </summary>
    int ReviewProfile(string title, string profileName, ProfileReview review, bool importing);

    /// <summary>
    /// Asks which game something belongs to, from a list of the existing games plus "no game" and
    /// an entry for typing a new one. Returns the game ("" = no game), or null if cancelled.
    /// </summary>
    string? ChooseGame(string title, string message, IReadOnlyList<string> games, string current, string noGameLabel, string confirmText);

    WindowInfo? PickWindow(Func<IReadOnlyList<WindowInfo>> listWindows);

    string? OpenFile(string title, string filter);

    string? SaveFile(string title, string filter, string suggestedName);
}
