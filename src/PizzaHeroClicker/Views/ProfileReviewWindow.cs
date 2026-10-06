using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Views;

/// <summary>
/// Shows what a profile would do before it is imported: anything risky first, in plain words,
/// with the choice to import it, import it with its key presses switched off, or not at all.
/// </summary>
public sealed class ProfileReviewWindow : Window
{
    /// <summary>0 = import as it is, 1 = import with key presses off, -1 = cancelled or just closed.</summary>
    public int Result { get; private set; } = -1;

    public ProfileReviewWindow(string title, string profileName, ProfileReview review, bool importing)
    {
        Title = title;
        Style = (Style)FindResource("AppWindow");
        SizeToContent = SizeToContent.Height;
        Width = 620;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowChrome.ApplyDarkTitleBar(this);

        var worst = review.Worst;
        var (verdict, verdictBrush) = worst switch
        {
            ReviewLevel.Danger => ("THIS PROFILE DOES THINGS A GAME PROFILE SHOULD NOT NEED", "TomatoBrush"),
            ReviewLevel.Caution => ("LOOK THIS OVER BEFORE RUNNING IT", "CheeseBrush"),
            _ => ("NOTHING UNUSUAL FOUND", "BasilBrush"),
        };

        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = verdict,
            FontFamily = (FontFamily)FindResource("HeadFont"),
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = (Brush)FindResource(verdictBrush),
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new TextBlock
        {
            Text = importing
                ? $"\"{profileName}\" is a script of mouse clicks and key presses. Only import profiles from people you trust. This is what it would do:"
                : $"This is what \"{profileName}\" does:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (importing)
        {
            // The default (Enter) button is the cautious choice whenever there is something to be cautious about.
            bool offerKeysOff = review.KeyActions > 0;
            bool risky = worst == ReviewLevel.Danger;
            var import = MakeButton(risky ? "IMPORT ANYWAY" : "IMPORT", 0, primary: !risky && !offerKeysOff, isDefault: !risky && !offerKeysOff);
            if (offerKeysOff)
            {
                var keysOff = MakeButton("IMPORT WITH KEY PRESSES OFF", 1, primary: true, isDefault: true);
                keysOff.ToolTip = "Imports it with every key-pressing action switched off. They stay in the Actions list, so you can read them and tick the ones you are happy with.";
                buttons.Children.Add(keysOff);
            }
            buttons.Children.Add(import);
            var cancel = new Button { Content = "CANCEL", MinWidth = 96, IsCancel = true, IsDefault = risky && !offerKeysOff, Margin = new Thickness(6, 0, 0, 0) };
            buttons.Children.Add(cancel);
        }
        else
        {
            buttons.Children.Add(new Button { Content = "CLOSE", MinWidth = 96, IsCancel = true, IsDefault = true, Margin = new Thickness(0) });
        }

        var footer = new StackPanel();
        if (importing)
        {
            footer.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 12, 0, 0),
                Text = "Whatever you choose, the imported profile gets your own hotkeys and keeps the mouse-to-corner emergency stop on. Nothing runs until you start it.",
            });
        }
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var list = new StackPanel();
        foreach (var note in review.Notes) list.Children.Add(NoteRow(note));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private Button MakeButton(string text, int result, bool primary, bool isDefault)
    {
        var button = new Button { Content = text, MinWidth = 96, IsDefault = isDefault, Margin = new Thickness(6, 0, 0, 0) };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        button.Click += (_, _) =>
        {
            Result = result;
            Close();
        };
        return button;
    }

    private Border NoteRow(ReviewNote note)
    {
        var (label, brush) = note.Level switch
        {
            ReviewLevel.Danger => ("RISK", "TomatoBrush"),
            ReviewLevel.Caution => ("CHECK", "CheeseBrush"),
            _ => ("INFO", "DimBrush"),
        };
        var row = new DockPanel();
        var tag = new TextBlock
        {
            Text = label,
            Width = 58,
            FontFamily = (FontFamily)FindResource("HeadFont"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(brush),
        };
        DockPanel.SetDock(tag, Dock.Left);
        row.Children.Add(tag);
        row.Children.Add(new TextBlock { Text = note.Text, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Child = row,
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource(note.Level == ReviewLevel.Danger ? "TomatoDarkBrush" : "LineBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
    }
}
