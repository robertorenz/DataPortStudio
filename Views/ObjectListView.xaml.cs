using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
}
