using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Views;

public partial class AboutTab : UserControl
{
    public AboutTab()
    {
        InitializeComponent();
        var version = typeof(AboutTab).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"version {version.Major}.{version.Minor}.{version.Build}";
        DataFolderText.Text = AppPaths.Root + (AppPaths.IsPortable ? "   (portable)" : "");
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // Only ever the fixed https links written in the XAML above.
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {e.Uri}", ex);
        }
        e.Handled = true;
    }
}
