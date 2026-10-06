using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PizzaHeroClicker.Views;

/// <summary>Themed replacement for MessageBox: a message, optional text input, and custom buttons.</summary>
public partial class PromptWindow : Window
{
    private int _result = -1;

    private PromptWindow(string title, string message, string? input, string[] buttons)
    {
        InitializeComponent();
        WindowChrome.ApplyDarkTitleBar(this);
        Title = title;
        MessageText.Text = message;

        if (input is not null)
        {
            InputBox.Visibility = Visibility.Visible;
            InputBox.Text = input;
            Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        }

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 84,
                Margin = new Thickness(6, 0, 0, 0),
                IsDefault = i == 0,
            };
            if (i == 0) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) => { _result = index; Close(); };
            ButtonRow.Children.Add(button);
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>Returns the index of the pressed button (-1 if closed) and the entered text.</summary>
    public static (int Button, string Text) Show(Window? owner, string title, string message, string? input, params string[] buttons)
    {
        var window = new PromptWindow(title, message, input, buttons);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return (window._result, window.InputBox.Text);
    }
}
