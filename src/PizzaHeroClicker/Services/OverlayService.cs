using PizzaHeroClicker.Views;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

/// <summary>A numbered marker at a physical virtual-screen pixel.</summary>
public readonly record struct OverlayMarker(string Label, int X, int Y);

/// <summary>Owns the always-on-top status pill and the per-monitor position overlays. UI thread only.</summary>
public sealed class OverlayService(ScreenService screen) : IDisposable
{
    private StatusOverlayWindow? _status;
    private readonly List<PositionOverlayWindow> _positions = new();
    private OverlayMarker[] _lastMarkers = [];
    private RECT[] _lastMonitors = [];

    /// <summary>The user dragged the status overlay to a new position (physical pixels).</summary>
    public event Action<int, int>? StatusMoved;

    public void UpdateStatus(bool visible, bool clickThrough, string kind, string text, int? x, int? y)
    {
        if (!visible)
        {
            _status?.Close();
            _status = null;
            return;
        }

        if (_status is null)
        {
            _status = new StatusOverlayWindow();
            _status.Moved += (mx, my) => StatusMoved?.Invoke(mx, my);
            _status.SetClickThrough(clickThrough);
            _status.SetPosition(x, y);
            _status.SetState(kind, text);
            _status.Show();
            return;
        }

        _status.SetClickThrough(clickThrough);
        _status.SetPosition(x, y);
        _status.SetState(kind, text);
    }

    /// <summary>Shows, updates or hides the numbered markers. Cheap to call repeatedly: it only redraws on change.</summary>
    public void UpdatePositions(bool visible, IReadOnlyList<OverlayMarker> markers)
    {
        if (!visible)
        {
            ClosePositions();
            return;
        }

        var monitors = screen.GetMonitors();
        bool monitorsChanged = !monitors.AsSpan().SequenceEqual(_lastMonitors, MonitorComparer.Instance);
        bool markersChanged = !markers.SequenceEqual(_lastMarkers);
        if (!monitorsChanged && !markersChanged && _positions.Count > 0) return;

        if (monitorsChanged || _positions.Count == 0)
        {
            ClosePositions();
            foreach (var monitor in monitors) _positions.Add(new PositionOverlayWindow(monitor));
        }
        _lastMonitors = monitors;
        _lastMarkers = markers.ToArray();

        foreach (var window in _positions)
        {
            // Each window only draws the markers on (or just beside) its own monitor.
            var m = window.Monitor;
            window.SetMarkers(_lastMarkers.Where(k => k.X >= m.Left - 40 && k.X < m.Right + 40 && k.Y >= m.Top - 40 && k.Y < m.Bottom + 40).ToList());
            if (!window.IsVisible) window.Show();
        }
    }

    private void ClosePositions()
    {
        foreach (var window in _positions) window.Close();
        _positions.Clear();
        _lastMarkers = [];
        _lastMonitors = [];
    }

    public void Dispose()
    {
        _status?.Close();
        _status = null;
        ClosePositions();
    }

    private sealed class MonitorComparer : IEqualityComparer<RECT>
    {
        public static readonly MonitorComparer Instance = new();
        public bool Equals(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        public int GetHashCode(RECT r) => HashCode.Combine(r.Left, r.Top, r.Right, r.Bottom);
    }
}
