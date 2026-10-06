using System.Windows;
using System.Windows.Input;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Views;

public partial class WindowPickerWindow : Window
{
    private readonly Func<IReadOnlyList<WindowInfo>> _listWindows;

    public WindowInfo? Selected { get; private set; }

    public WindowPickerWindow(Func<IReadOnlyList<WindowInfo>> listWindows)
    {
        InitializeComponent();
        WindowChrome.ApplyDarkTitleBar(this);
        _listWindows = listWindows;
        Reload();
    }

    private void Reload() => List.ItemsSource = _listWindows().OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not WindowInfo info) return;
        Selected = info;
        DialogResult = true;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnAccept(sender, e);
}
