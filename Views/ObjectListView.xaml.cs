using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class ObjectListView : UserControl
{
    public ObjectListView()
    {
        InitializeComponent();
    }

    /// <summary>Select the row under the cursor before the context menu opens.</summary>
    private void Grid_RightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            row.IsSelected = true;
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ObjectListViewModel vm && vm.SelectedItem is not null && vm.OpenCommand.CanExecute(null))
            vm.OpenCommand.Execute(null);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not ObjectListViewModel vm) return;
        if (e.Key == Key.C) { vm.CopyCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.V) { vm.PasteCommand.Execute(null); e.Handled = true; }
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObjectsGrid.SelectedItem is null) return;
        // Defer until WPF has generated the virtualized row, then bring the located object onscreen.
        Dispatcher.BeginInvoke(new Action(() =>
            ObjectsGrid.ScrollIntoView(ObjectsGrid.SelectedItem)), DispatcherPriority.Background);
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ObjectListViewModel vm) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            LocatorBox.Focus();
            LocatorBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3 || (LocatorBox.IsKeyboardFocusWithin && e.Key == Key.Enter))
        {
            if (!string.IsNullOrWhiteSpace(vm.LocatorText)) vm.LocateNextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (LocatorBox.IsKeyboardFocusWithin && e.Key == Key.Escape)
        {
            vm.ClearLocatorCommand.Execute(null);
            e.Handled = true;
        }
    }
}
