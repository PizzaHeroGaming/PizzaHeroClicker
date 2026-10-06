using System.Runtime.InteropServices;
using System.Windows.Interop;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

public sealed record TrayMenuState(bool IsRunning, IReadOnlyList<ProfileRef> Profiles, ProfileRef Current);

/// <summary>
/// The notification-area icon and its right-click menu, via Shell_NotifyIcon and a native
/// popup menu. Lives on the UI thread; events are raised there.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;
    private const int CmdShow = 1, CmdToggle = 2, CmdExit = 3, CmdFirstProfile = 100;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _icon;
    private bool _added;
    private string _tooltip = "Pizza Hero Clicker";

    public event Action? ShowRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public event Action<ProfileRef>? ProfileRequested;

    /// <summary>Supplies the current state each time the menu opens.</summary>
    public Func<TrayMenuState>? MenuState { get; set; }

    public TrayService()
    {
        // A hidden top-level window (not message-only): it must be able to become foreground so
        // the popup menu closes when the user clicks elsewhere, and it must receive the
        // "TaskbarCreated" broadcast.
        _source = new HwndSource(new HwndSourceParameters("PizzaHeroClicker.Tray")
        {
            WindowStyle = unchecked((int)0x80000000),            // WS_POPUP
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Reuse the exe's own icon (embedded by <ApplicationIcon>).
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, null, small, 1) > 0) _icon = small[0];
    }

    public void Show()
    {
        var data = CreateData();
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added) Log.Warn("The tray icon could not be added (is Explorer running?).");
    }

    public void SetTooltip(string text)
    {
        if (text.Length > 127) text = text[..127]; // szTip holds 128 characters including the terminator
        if (text == _tooltip) return;
        _tooltip = text;
        if (!_added) return;
        var data = CreateData();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch (lParam.ToInt64() & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowRequested?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
        }
        else if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            Show(); // Explorer restarted and forgot every tray icon
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var state = MenuState?.Invoke() ?? new TrayMenuState(false, [], "");
        IntPtr menu = CreatePopupMenu();
        IntPtr profiles = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, CmdShow, "Open Pizza Hero Clicker");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, CmdToggle, state.IsRunning ? "Stop" : "Start");

            // With more than one game, each game gets its own submenu. "&" marks a keyboard
            // accelerator in native menus, so it is doubled to show literally.
            bool byGame = state.Profiles.Select(p => p.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var gameMenus = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < state.Profiles.Count; i++)
            {
                var profile = state.Profiles[i];
                IntPtr parent = profiles;
                if (byGame && !gameMenus.TryGetValue(profile.Game, out parent))
                {
                    parent = gameMenus[profile.Game] = CreatePopupMenu();
                    AppendMenu(profiles, MF_POPUP, (UIntPtr)(nuint)parent, (profile.Game.Length == 0 ? "(No game)" : profile.Game).Replace("&", "&&"));
                }
                AppendMenu(parent, MF_STRING | (profile.Is(state.Current) ? MF_CHECKED : 0), (UIntPtr)(uint)(CmdFirstProfile + i),
                    profile.Name.Replace("&", "&&"));
            }
            AppendMenu(menu, MF_POPUP | (state.Profiles.Count == 0 ? MF_GRAYED : 0), (UIntPtr)(nuint)profiles, "Profile");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, CmdExit, "Exit");

            GetCursorPos(out var p);
            SetForegroundWindow(_source.Handle);
            int command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero); // documented fix for menus that won't dismiss

            switch (command)
            {
                case CmdShow: ShowRequested?.Invoke(); break;
                case CmdToggle: ToggleRequested?.Invoke(); break;
                case CmdExit: ExitRequested?.Invoke(); break;
                case >= CmdFirstProfile when command - CmdFirstProfile < state.Profiles.Count:
                    ProfileRequested?.Invoke(state.Profiles[command - CmdFirstProfile]);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu); // also destroys the attached submenu
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
