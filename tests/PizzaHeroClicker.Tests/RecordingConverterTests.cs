using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class RecordingConverterTests
{
    private const int Shift = 0xA0, Ctrl = 0xA2, KeyC = 0x43, KeyR = 0x52, KeyW = 0x57;

    private static RawEvent Move(double t, int x, int y) => new(t, RawKind.MouseMove, x, y, 0);
    private static RawEvent Down(double t, int x, int y, int button = 0) => new(t, RawKind.MouseDown, x, y, button);
    private static RawEvent Up(double t, int x, int y, int button = 0) => new(t, RawKind.MouseUp, x, y, button);
    private static RawEvent KeyDown(double t, int vk) => new(t, RawKind.KeyDown, 0, 0, vk);
    private static RawEvent KeyUp(double t, int vk) => new(t, RawKind.KeyUp, 0, 0, vk);
    private static RawEvent Wheel(double t, int delta) => new(t, RawKind.Wheel, 300, 300, delta);

    [Fact]
    public void ClicksKeepPositionHoldAndGaps()
    {
        var actions = RecordingConverter.Convert(
        [
            Down(100, 50, -60), Up(180, 51, -60),          // click, held 80 ms, on a monitor above the primary
            Down(680, 400, 400, 1), Up(700, 400, 400, 1),  // right click 500 ms later
        ], new RecordingOptions());

        Assert.Equal(2, actions.Count);
        var first = Assert.IsType<ClickAction>(actions[0]);
        Assert.Equal((50, -60, ClickButton.Left, 80), (first.X, first.Y, first.Button, first.HoldMs));
        Assert.Equal(500, first.IntervalMs);

        var second = Assert.IsType<ClickAction>(actions[1]);
        Assert.Equal(ClickButton.Right, second.Button);
        Assert.Equal(0, second.IntervalMs); // nothing follows the last action
    }

    [Fact]
    public void PressAndReleaseFarApartIsADrag()
    {
        var actions = RecordingConverter.Convert(
        [
            Down(0, 10, 10), Move(50, 60, 60), Move(100, 200, 150), Up(300, 200, 150),
        ], new RecordingOptions());

        var drag = Assert.IsType<DragAction>(Assert.Single(actions));
        Assert.Equal((10, 10, 200, 150, 300), (drag.X1, drag.Y1, drag.X2, drag.Y2, drag.DurationMs));
    }

    [Fact]
    public void KeysCarryTheirModifiersAndLoneModifiersCount()
    {
        var actions = RecordingConverter.Convert(
        [
            KeyDown(0, Ctrl), KeyDown(40, KeyC), KeyDown(70, KeyC) /* auto-repeat */, KeyUp(100, KeyC), KeyUp(120, Ctrl),
            KeyDown(500, Shift), KeyUp(900, Shift),   // Shift tapped on its own
            KeyDown(1000, KeyW), KeyUp(1250, KeyW),
        ], new RecordingOptions());

        Assert.Equal(3, actions.Count);
        var copy = Assert.IsType<KeyPressAction>(actions[0]);
        Assert.Equal("Ctrl+C", Assert.Single(copy.Keys).ToString());
        Assert.Equal(60, copy.HoldMs);
        Assert.Equal(400, copy.IntervalMs);

        var shift = Assert.IsType<KeyPressAction>(actions[1]);
        Assert.Equal("LeftShift", shift.Keys[0].ToString());
        Assert.Equal(400, shift.HoldMs);

        Assert.Equal("W", Assert.IsType<KeyPressAction>(actions[2]).Keys[0].ToString());
    }

    [Fact]
    public void TheAppsOwnHotkeysAreLeftOut()
    {
        var options = new RecordingOptions { IgnoredCombos = new HashSet<KeyCombo> { KeyCombo.Parse("Ctrl+Shift+R") } };
        var actions = RecordingConverter.Convert(
        [
            KeyUp(5, KeyR), KeyUp(8, Shift), KeyUp(9, Ctrl),          // tail of the hotkey that STARTED the recording
            Down(100, 5, 5), Up(150, 5, 5),
            KeyDown(900, Ctrl), KeyDown(910, Shift), KeyDown(950, KeyR), // the hotkey that stops it (no key-ups arrive)
        ], options);

        Assert.IsType<ClickAction>(Assert.Single(actions));
    }

    [Fact]
    public void MovementIsThinnedButEndsWhereTheCursorStopped()
    {
        var events = new List<RawEvent>();
        for (int i = 0; i <= 20; i++) events.Add(Move(i * 10, i * 5, 0)); // 200 ms of movement sampled every 10 ms
        events.Add(Down(260, 100, 0));
        events.Add(Up(300, 100, 0));

        var actions = RecordingConverter.Convert(events, new RecordingOptions(MoveSampleMs: 40));
        var moves = actions.OfType<MoveAction>().ToList();

        Assert.InRange(moves.Count, 5, 7);                       // about one per 40 ms, not 21
        Assert.Equal((100, 0), (moves[^1].X, moves[^1].Y));      // the final position is never dropped
        Assert.All(moves.Skip(1), m => Assert.InRange(m.DurationMs, 1, 40));
        Assert.IsType<ClickAction>(actions[^1]);

        // Total playback time (durations + delays) matches the recording to within rounding.
        double total = actions.Sum(a => (a.IntervalMs ?? 0) + (a is MoveAction m ? m.DurationMs : a is ClickAction c ? c.HoldMs : 0));
        Assert.InRange(total, 295, 305);
    }

    [Fact]
    public void WheelTicksMergeAndLongPausesCanBeTrimmed()
    {
        var actions = RecordingConverter.Convert(
        [
            Wheel(0, -120), Wheel(30, -120), Wheel(60, -240),   // one flick down: 4 notches
            Wheel(5000, 120),                                    // much later, one notch up
        ], new RecordingOptions(TrimDelays: true, MaxDelayMs: 750));

        Assert.Equal(2, actions.Count);
        var down = Assert.IsType<ScrollAction>(actions[0]);
        Assert.Equal((ScrollDirection.Down, 4, false), (down.Direction, down.Amount, down.UseCursor));
        Assert.Equal(750, down.IntervalMs); // 4940 ms pause trimmed
        Assert.Equal(ScrollDirection.Up, Assert.IsType<ScrollAction>(actions[1]).Direction);
    }

    [Fact]
    public void EmptyOrMeaninglessRecordingsProduceNothing()
    {
        Assert.Empty(RecordingConverter.Convert([], new RecordingOptions()));
        Assert.Empty(RecordingConverter.Convert([Up(10, 1, 1), KeyUp(20, KeyC)], new RecordingOptions()));
    }
}
