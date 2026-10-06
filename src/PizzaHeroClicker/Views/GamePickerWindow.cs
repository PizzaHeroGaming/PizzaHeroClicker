using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PizzaHeroClicker.Views;

/// <summary>
/// Asks which game something belongs to: a drop-down of the existing games (plus "no game"),
/// with a last entry that reveals a box for typing a new game's name.
/// </summary>
public sealed class GamePickerWindow : Window
{
    private const string NewGameEntry = "+ New game…";

    private readonly ComboBox _games;
    private readonly TextBox _newName;
    private readonly StackPanel _newNameRow;
    private readonly string _noGameLabel;

    /// <summary>The chosen game ("" = no game), or null if cancelled.</summary>
    public string? Result { get; private set; }

    public GamePickerWindow(string title, string message, IReadOnlyList<string> games, string current, string noGameLabel, string confirmText)
    {
        _noGameLabel = noGameLabel;
        Title = title;
        Style = (Style)FindResource("AppWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 380;
        MaxWidth = 560;
        WindowChrome.ApplyDarkTitleBar(this);

        _games = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
        _games.Items.Add(noGameLabel);
        foreach (string game in games.Where(g => g.Length > 0)) _games.Items.Add(game);
        _games.Items.Add(NewGameEntry);
        _games.SelectedItem = current.Length == 0
            ? noGameLabel
            : games.FirstOrDefault(g => string.Equals(g, current, StringComparison.OrdinalIgnoreCase)) ?? noGameLabel;

        _newName = new TextBox { MinWidth = 320 };
        _newNameRow = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _newNameRow.Children.Add(new TextBlock { Text = "Name of the new game", Style = (Style)FindResource("FieldLabel") });
        _newNameRow.Children.Add(_newName);

        _games.SelectionChanged += (_, _) =>
        {
            bool isNew = Equals(_games.SelectedItem, NewGameEntry);
            _newNameRow.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            if (isNew) Dispatcher.BeginInvoke(() => _newName.Focus());
        };

        var confirm = new Button { Content = confirmText, MinWidth = 96, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        confirm.Click += (_, _) => Accept();
        var cancel = new Button { Content = "CANCEL", MinWidth = 96, IsCancel = true, Margin = new Thickness(0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "Game", Style = (Style)FindResource("FieldLabel") });
        root.Children.Add(_games);
        root.Children.Add(_newNameRow);
        root.Children.Add(buttons);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Accept()
    {
        if (Equals(_games.SelectedItem, NewGameEntry))
        {
            string name = _newName.Text.Trim();
            if (name.Length == 0)
            {
                _newName.Focus(); // nothing typed yet: stay open
                return;
            }
            Result = name;
        }
        else
        {
            string? chosen = _games.SelectedItem as string;
            Result = chosen is null || chosen == _noGameLabel ? "" : chosen;
        }
        DialogResult = true;
    }
}
