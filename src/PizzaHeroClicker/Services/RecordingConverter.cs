using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <param name="TrimDelays">Cap every pause between actions at <paramref name="MaxDelayMs"/>.</param>
/// <param name="MoveSampleMs">Keep at most one cursor position per this many milliseconds.</param>
/// <param name="ClickSlopPx">A press and release further apart than this is a drag, not a click.</param>
public sealed record RecordingOptions(bool TrimDelays = false, int MaxDelayMs = 1000, int MoveSampleMs = 40, int ClickSlopPx = 5)
{
    /// <summary>Key combinations to leave out of the result (the app's own hotkeys).</summary>
    public IReadOnlySet<KeyCombo> IgnoredCombos { get; init; } = new HashSet<KeyCombo>();
}

/// <summary>
/// Turns a raw recording into ordinary, editable actions:
///   press + release at one spot  -> Click (hold = how long the button was down)
///   press + release elsewhere    -> Drag
///   wheel ticks                  -> Scroll (consecutive ticks merged)
///   key down + up                -> KeyPress (with the modifiers held at the time)
///   cursor movement              -> Move (thinned to one sample every MoveSampleMs)
/// Each action's "delay afterwards" is the real pause before the next recorded action, so
/// playing the list back reproduces the original timing. Pure function: no I/O, no clock.
///
/// Limitation: actions play back one after another, so inputs that overlapped in time
/// (holding W while clicking) become sequential.
/// </summary>
public static class RecordingConverter
{
    private const double ScrollMergeMs = 150;

    private sealed class Item(double start, double end, ActionBase action)
    {
        public double Start = start;
        public double End = end;
        public ActionBase Action = action;
    }

    public static List<ActionBase> Convert(IReadOnlyList<RawEvent> events, RecordingOptions options)
    {
        var items = new List<Item>();
        var buttonsDown = new Dictionary<int, RawEvent>();
        var keysDown = new Dictionary<int, (double Time, KeyMods Mods)>();
        var modifiersDown = new Dictionary<int, (double Time, bool Used)>();
        double lastEnd = events.Count > 0 ? events[0].TimeMs : 0;
        double lastMoveSample = double.NegativeInfinity;
        RawEvent? skippedMove = null;

        void AddMove(RawEvent e)
        {
            // Glide over the time since the previous sample, so thinned movement stays smooth.
            double duration = Math.Clamp(e.TimeMs - lastEnd, 0, options.MoveSampleMs);
            items.Add(new Item(e.TimeMs - duration, e.TimeMs, new MoveAction { X = e.X, Y = e.Y, DurationMs = (int)Math.Round(duration) }));
            lastEnd = lastMoveSample = e.TimeMs;
            skippedMove = null;
        }

        // Make sure the cursor reaches its final resting point before something happens there.
        void FlushMove()
        {
            if (skippedMove is { } pending) AddMove(pending);
        }

        void AddKey(KeyCombo combo, double start, double end)
        {
            if (options.IgnoredCombos.Contains(combo)) return;
            var action = new KeyPressAction { HoldMs = (int)Math.Round(end - start) };
            action.Keys.Add(combo);
            items.Add(new Item(start, end, action));
            lastEnd = Math.Max(lastEnd, end);
        }

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RawKind.MouseMove:
                    if (buttonsDown.Count > 0) break; // movement with a button held is part of a drag
                    if (e.TimeMs - lastMoveSample < options.MoveSampleMs) skippedMove = e;
                    else AddMove(e);
                    break;

                case RawKind.MouseDown:
                    FlushMove();
                    buttonsDown[e.Data] = e;
                    break;

                case RawKind.MouseUp:
                {
                    if (!buttonsDown.Remove(e.Data, out var down)) break; // release without a recorded press
                    var button = (ClickButton)Math.Clamp(e.Data, 0, 2);
                    int held = (int)Math.Round(e.TimeMs - down.TimeMs);
                    bool moved = Math.Abs(e.X - down.X) > options.ClickSlopPx || Math.Abs(e.Y - down.Y) > options.ClickSlopPx;
                    ActionBase action = moved
                        ? new DragAction { X1 = down.X, Y1 = down.Y, X2 = e.X, Y2 = e.Y, Button = button, DurationMs = held }
                        : new ClickAction { X = down.X, Y = down.Y, Button = button, HoldMs = held };
                    items.Add(new Item(down.TimeMs, e.TimeMs, action));
                    lastEnd = Math.Max(lastEnd, e.TimeMs);
                    break;
                }

                case RawKind.Wheel or RawKind.HWheel:
                {
                    if (e.Data == 0) break;
                    FlushMove();
                    var direction = e.Kind == RawKind.Wheel
                        ? (e.Data > 0 ? ScrollDirection.Up : ScrollDirection.Down)
                        : (e.Data > 0 ? ScrollDirection.Right : ScrollDirection.Left);
                    int notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Data) / 120.0));

                    // A flick of the wheel arrives as many events: fold them into one action.
                    if (items.Count > 0 && items[^1].Action is ScrollAction previous && previous.Direction == direction
                        && e.TimeMs - items[^1].End <= ScrollMergeMs)
                    {
                        previous.Amount += notches;
                        items[^1].End = e.TimeMs;
                    }
                    else
                    {
                        items.Add(new Item(e.TimeMs, e.TimeMs,
                            new ScrollAction { Direction = direction, Amount = notches, UseCursor = false, X = e.X, Y = e.Y }));
                    }
                    lastEnd = Math.Max(lastEnd, e.TimeMs);
                    break;
                }

                case RawKind.KeyDown:
                    if (IsModifier(e.Data))
                    {
                        modifiersDown.TryAdd(e.Data, (e.TimeMs, false));
                    }
                    else if (!keysDown.ContainsKey(e.Data)) // ignore keyboard auto-repeat
                    {
                        keysDown[e.Data] = (e.TimeMs, CurrentMods(modifiersDown.Keys));
                        foreach (int mod in modifiersDown.Keys.ToList())
                            modifiersDown[mod] = (modifiersDown[mod].Time, true);
                    }
                    break;

                case RawKind.KeyUp:
                    if (IsModifier(e.Data))
                    {
                        // A modifier tapped on its own (e.g. Shift to sprint) is a key press in its own right.
                        if (modifiersDown.Remove(e.Data, out var mod) && !mod.Used) AddKey(new KeyCombo(e.Data), mod.Time, e.TimeMs);
                    }
                    else if (keysDown.Remove(e.Data, out var key))
                    {
                        AddKey(new KeyCombo(e.Data, key.Mods), key.Time, e.TimeMs);
                    }
                    break;
            }
        }

        // Order by when each action began, then turn the gaps into per-action delays.
        var ordered = items.OrderBy(i => i.Start).ToList(); // OrderBy is stable
        for (int i = 0; i < ordered.Count; i++)
        {
            double gap = i + 1 < ordered.Count ? Math.Max(0, ordered[i + 1].Start - ordered[i].End) : 0;
            if (options.TrimDelays) gap = Math.Min(gap, options.MaxDelayMs);
            ordered[i].Action.IntervalMs = (int)Math.Round(gap);
        }
        return ordered.Select(i => i.Action).ToList();
    }

    private static bool IsModifier(int vk) => ModOf(vk) != KeyMods.None;

    private static KeyMods ModOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => KeyMods.Shift,
        0x11 or 0xA2 or 0xA3 => KeyMods.Ctrl,
        0x12 or 0xA4 or 0xA5 => KeyMods.Alt,
        0x5B or 0x5C => KeyMods.Win,
        _ => KeyMods.None,
    };

    private static KeyMods CurrentMods(IEnumerable<int> modifierKeys)
    {
        var mods = KeyMods.None;
        foreach (int vk in modifierKeys) mods |= ModOf(vk);
        return mods;
    }
}
