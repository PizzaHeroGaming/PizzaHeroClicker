using System.Windows;
using PizzaHeroClicker.ViewModels;

namespace PizzaHeroClicker.Views;

public partial class ActionEditorWindow : Window
{
    public ActionEditorWindow(ActionEditorViewModel viewModel)
    {
        InitializeComponent();
        WindowChrome.ApplyDarkTitleBar(this);
        DataContext = viewModel;
        MaxHeight = SystemParameters.WorkArea.Height; // never taller than the screen; the content scrolls instead
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (((ActionEditorViewModel)DataContext).Validate()) DialogResult = true;
    }
}
