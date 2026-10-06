using System.Runtime.InteropServices;
using System.Windows.Threading;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

public enum RawKind { MouseMove, MouseDown, MouseUp, Wheel, HWheel, KeyDown, KeyUp }

/// <summary>
/// One captured input event. <see cref="TimeMs"/> is milliseconds since the recording began.
/// <see cref="Data"/> is the button (0 left, 1 right, 2 middle), the wheel delta, or the virtual-key code.
/// </summary>
public readonly record struct RawEvent(double TimeMs, RawKind Kind, int X, int Y, int Data);

/// <summary>
/// Records real mouse and keyboard input with low-level hooks (TinyTask style).
///
/// The hooks live on their own thread with its own message loop. Windows calls a low-level
/// hook synchronously for every input event system-wide, so the callback must never wait on
/// the UI thread: a busy UI would otherwise make the whole desktop's mouse lag.
/// </summary>
public sealed class RecorderService : IDisposable
{
    private const int MaxEvents = 500_000;
    private const double MoveThrottleMs = 10;

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    // Delegates handed to SetWindowsHookEx must stay referenced or the GC collects them mid-recording.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _mouseHook, _keyboardHook;
    private List<RawEvent> _events = new();
    private long _startTicks;
    private bool _recordMoves;
    private double _lastMoveMs;
    private int _ownButtonsDown;     // bitmask of buttons pressed on our own windows
    private int _eventCount;

    public bool IsRecording { get; private set; }

    /// <summary>When the current / last recording began, as epoch milliseconds (UTC).</summary>
    public long StartedAtEpochMs { get; private set; }

    /// <summary>Events captured so far. Safe to read from any thread.</summary>
    public int EventCount => Volatile.Read(ref _eventCount);

    public RecorderService()
    {
        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
    }

    /// <summary>Starts recording. Returns false if the hooks could not be installed.</summary>
    public bool Start(bool recordMouseMoves)
    {
        if (IsRecording) return true;

        _events = new List<RawEvent>(4096);
        _recordMoves = recordMouseMoves;
        _lastMoveMs = double.NegativeInfinity;
        _ownButtonsDown = 0;
        Volatile.Write(ref _eventCount, 0);

        using var ready = new ManualResetEventSlim();
        bool installed = false;
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            IntPtr module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            installed = _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;
            if (!installed) Log.Error($"Could not install the recording hooks (error {Marshal.GetLastWin32Error()}).");

            _startTicks = TimingEngine.Now;
            ready.Set();
            if (installed) Dispatcher.Run(); // message loop: the hooks are serviced from here

            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = _keyboardHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Recorder",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();

        if (!installed)
        {
            _thread.Join();
            _thread = null;
            return false;
        }

        StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IsRecording = true;
        return true;
    }

    /// <summary>Stops recording and returns everything captured, in order.</summary>
    public IReadOnlyList<RawEvent> Stop()
    {
        if (!IsRecording) return [];
        IsRecording = false;

        _dispatcher?.InvokeShutdown(); // ends Dispatcher.Run on the hook thread, which then unhooks
        _thread?.Join(2000);
        _thread = null;
        _dispatcher = null;
        return _events; // the hook thread has exited, so nothing else touches the list
    }

    private double NowMs => TimingEngine.TicksToMs(TimingEngine.Now - _startTicks);

    private void Add(RawKind kind, int x, int y, int data)
    {
        if (_events.Count >= MaxEvents) return;
        _events.Add(new RawEvent(NowMs, kind, x, y, data));
        Interlocked.Increment(ref _eventCount);
    }

    // Both callbacks run on the recorder thread and do only trivial work before returning.

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Skip synthetic input (ours, or another automation tool's): record what the user physically did.
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                        if (_recordMoves)
                        {
                            double now = NowMs;
                            if (now - _lastMoveMs >= MoveThrottleMs)
                            {
                                _lastMoveMs = now;
                                Add(RawKind.MouseMove, data.pt.X, data.pt.Y, 0);
                            }
                        }
                        break;

                    case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                    {
                        int button = msg == WM_LBUTTONDOWN ? 0 : msg == WM_RBUTTONDOWN ? 1 : 2;
                        // Clicks on the clicker's own windows (e.g. the Stop Recording button) are not part of the macro.
                        if (IsOwnWindowAt(data.pt)) _ownButtonsDown |= 1 << button;
                        else Add(RawKind.MouseDown, data.pt.X, data.pt.Y, button);
                        break;
                    }

                    case WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP:
                    {
                        int button = msg == WM_LBUTTONUP ? 0 : msg == WM_RBUTTONUP ? 1 : 2;
                        if ((_ownButtonsDown & (1 << button)) != 0) _ownButtonsDown &= ~(1 << button);
                        else Add(RawKind.MouseUp, data.pt.X, data.pt.Y, button);
                        break;
                    }

                    case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
                        if (!IsOwnWindowAt(data.pt))
                        {
                            int delta = (short)(data.mouseData >> 16); // signed high word
                            Add(msg == WM_MOUSEWHEEL ? RawKind.Wheel : RawKind.HWheel, data.pt.X, data.pt.Y, delta);
                        }
                        break;
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) == 0)
            {
                switch (wParam.ToInt32())
                {
                    case WM_KEYDOWN or WM_SYSKEYDOWN:
                        Add(RawKind.KeyDown, 0, 0, (int)data.vkCode);
                        break;
                    case WM_KEYUP or WM_SYSKEYUP:
                        Add(RawKind.KeyUp, 0, 0, (int)data.vkCode);
                        break;
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool IsOwnWindowAt(POINT point)
    {
        IntPtr hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == OwnProcessId;
    }

    public void Dispose() => Stop();
}
