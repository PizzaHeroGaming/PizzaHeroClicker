using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PizzaHeroClicker.Models;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

/// <param name="Name">Identifier passed back in the Pressed / Released events.</param>
/// <param name="Label">Human-readable name for warnings.</param>
/// <param name="TrackRelease">Also raise Released when the key comes back up (hold-to-run).</param>
public sealed record HotkeyRequest(string Name, string Label, KeyCombo Combo, bool TrackRelease = false);

/// <summary>
/// System-wide hotkeys via RegisterHotKey. They fire while the app is unfocused, minimised
/// to the tray, or behind a fullscreen game. Lives on the UI thread; events are raised there.
///
/// RegisterHotKey only reports key-down, so for hold-to-run the service polls
/// GetAsyncKeyState after a press until the key is released. That avoids installing a
/// low-level keyboard hook, which would sit in the input path of every keystroke.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyRequest> _registered = new();
    private readonly DispatcherTimer _releaseTimer;
    private List<HotkeyRequest> _requested = new();
    private HotkeyRequest? _held;
    private bool _suspended;
    private int _nextId = 0x2000;

    public event Action<string>? Pressed;
    public event Action<string>? Released;

    public HotkeyService()
    {
        // A message-only window (parent HWND_MESSAGE = -3): invisible, exists just to receive WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("PizzaHeroClicker.Hotkeys")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);

        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _releaseTimer.Tick += (_, _) => CheckRelease();
    }

    /// <summary>
    /// Replaces every registration. Returns the requests Windows refused, which means another
    /// program already owns that combination.
    /// </summary>
    public IReadOnlyList<HotkeyRequest> Apply(IEnumerable<HotkeyRequest> requests)
    {
        _requested = requests.Where(r => !r.Combo.IsEmpty).ToList();
        return _suspended ? [] : RegisterAll();
    }

    /// <summary>Temporarily releases all hotkeys so a key press can reach a rebind box instead.</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public IReadOnlyList<HotkeyRequest> Resume()
    {
        _suspended = false;
        return RegisterAll();
    }

    private List<HotkeyRequest> RegisterAll()
    {
        UnregisterAll();
        var failed = new List<HotkeyRequest>();
        foreach (var request in _requested)
        {
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, (uint)request.Combo.Mods | MOD_NOREPEAT, (uint)request.Combo.Vk))
            {
                _registered[id] = request;
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Log.Warn($"Hotkey {request.Combo} for '{request.Label}' could not be registered (error {error}).");
                failed.Add(request);
            }
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        EndHold(); // a held hold-to-run key must not leave the engine running
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var request))
        {
            handled = true;
            if (request.TrackRelease)
            {
                EndHold();
                _held = request;
                _releaseTimer.Start();
            }
            Pressed?.Invoke(request.Name);
        }
        return IntPtr.Zero;
    }

    private void CheckRelease()
    {
        if (_held is null || (GetAsyncKeyState(_held.Combo.Vk) & 0x8000) == 0) EndHold();
    }

    private void EndHold()
    {
        _releaseTimer.Stop();
        var held = _held;
        _held = null;
        if (held is not null) Released?.Invoke(held.Name);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
