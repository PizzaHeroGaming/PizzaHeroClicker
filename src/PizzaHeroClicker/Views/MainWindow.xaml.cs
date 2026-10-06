using System.Windows;
using System.Windows.Controls;

namespace PizzaHeroClicker.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowChrome.ApplyDarkTitleBar(this);
        Closing += (_, e) =>
        {
            // Unsaved profile changes: let the user save, discard, or stay.
            if (DataContext is ViewModels.MainViewModel vm && !vm.ConfirmClose()) e.Cancel = true;
        };
        StateChanged += (_, _) =>
        {
            // Minimise to the tray: hide the window entirely so it leaves the taskbar.
            if (WindowState == WindowState.Minimized && DataContext is ViewModels.MainViewModel { Settings.MinimizeToTray: true }) Hide();
        };
    }

    /// <summary>Brings the window back from the tray or the taskbar.</summary>
    public void Restore()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnProfileMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = ProfileMenuButton.ContextMenu!;
        menu.DataContext = DataContext; // a context menu is outside the visual tree
        menu.PlacementTarget = ProfileMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    internal TabControl TabHost => Tabs;
    internal FrameworkElement RootElement => Root;
}
