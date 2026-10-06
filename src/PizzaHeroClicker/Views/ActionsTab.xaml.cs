using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.ViewModels;

namespace PizzaHeroClicker.Views;

public partial class ActionsTab : UserControl
{
    private Point _dragStart;
    private ActionBase? _dragCandidate;

    public ActionsTab() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var menu = AddButton.ContextMenu!;
        menu.DataContext = DataContext; // a context menu is outside the visual tree
        menu.PlacementTarget = AddButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e) => ViewModel?.NotifyActionEdited();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is not null && !IsInside<CheckBox>(e.OriginalSource))
            ViewModel?.EditActionCommand.Execute(null);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) ViewModel?.DeleteActionCommand.Execute(null);
        else if (e.Key == Key.Enter) ViewModel?.EditActionCommand.Execute(null);
    }

    // ------------------------------------------------------------------ drag to reorder

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = IsInside<CheckBox>(e.OriginalSource) || IsInside<ScrollBar>(e.OriginalSource) ? null : ItemAt(e.OriginalSource);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(List, new DataObject(typeof(ActionBase), item), DragDropEffects.Move);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ActionBase)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.Data.GetData(typeof(ActionBase)) is not ActionBase dragged) return;
        var actions = vm.Profile.Actions;
        int from = actions.IndexOf(dragged);
        int to = ItemAt(e.OriginalSource) is { } target ? actions.IndexOf(target) : actions.Count - 1;
        vm.MoveAction(from, to);
        e.Handled = true;
    }

    /// <summary>The action whose row contains the given element, if any.</summary>
    private static ActionBase? ItemAt(object source) => FindAncestor<ListBoxItem>(source)?.DataContext as ActionBase;

    private static bool IsInside<T>(object source) where T : DependencyObject => FindAncestor<T>(source) is not null;

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
        {
            // Text runs are not visuals, so fall back to the logical tree for them.
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as T;
    }
}
