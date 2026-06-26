using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DataPortStudio.Models;
using DataPortStudio.Services;
using DataPortStudio.ViewModels;

namespace DataPortStudio;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Vm.SelectedNode = e.NewValue as DbTreeNode;
    }

    private void UsersButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = Vm.SelectedNode;
        if (node is null) return;

        if (node.IsOpenable && Vm.OpenTableCommand.CanExecute(node))
            Vm.OpenTableCommand.Execute(node);
        else if (node.Type is NodeType.Function or NodeType.Procedure && Vm.EditRoutineCommand.CanExecute(node))
            Vm.EditRoutineCommand.Execute(node);
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var node = Vm.SelectedNode;
        if (node is null) return;
        if (e.Key == Key.C) { Run(Vm.CopyTableCommand, node); e.Handled = true; }
        else if (e.Key == Key.V) { Run(Vm.PasteTableCommand, node); e.Handled = true; }
    }

    private void Tree_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
        if (item.DataContext is not DbTreeNode node) return;

        var menu = BuildNodeMenu(node);
        if (menu is null) return;
        menu.PlacementTarget = item;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu? BuildNodeMenu(DbTreeNode node)
    {
        var menu = new ContextMenu();

        static string T(string key) => LocalizationManager.Instance[key];

        MenuItem Item(string headerKey, Action action)
        {
            var mi = new MenuItem { Header = T(headerKey) };
            mi.Click += (_, _) => action();
            return mi;
        }

        switch (node.Type)
        {
            // SQLite — same actions as the Objects list: Open, Design, Copy, Paste, Delete.
            case NodeType.Table when node.Connection.Engine == DatabaseEngine.Sqlite:
                AddTableMenu(menu, node, canDesign: true, canDrop: true, sqlServerExtras: false);
                break;

            case NodeType.Table when node.Connection.Engine == DatabaseEngine.Firebird:
                AddTableMenu(menu, node, canDesign: false, canDrop: true, sqlServerExtras: false);
                break;

            // MongoDB is read-only (and can't be dropped here): open and copy/paste collections only.
            case NodeType.Table when node.Connection.Engine == DatabaseEngine.MongoDb:
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                AddCopyPaste(menu, node);
                break;

            // Excel file node: open all sheets as tabs.
            case NodeType.Table when node.Connection.Engine == DatabaseEngine.Excel:
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                break;

            // Clarion files are read-only: open, or copy out to a SQL database. No paste into them.
            case NodeType.Table when node.Connection.Engine.IsClarionFile():
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_CopyTable", () => Run(Vm.CopyTableCommand, node)));
                break;

            case NodeType.Table when node.Connection.Engine == DatabaseEngine.Oracle:
                AddTableMenu(menu, node, canDesign: false, canDrop: true, sqlServerExtras: false);
                break;

            case NodeType.Table when node.Connection.Engine.IsMySql():
                AddTableMenu(menu, node, canDesign: false, canDrop: true, sqlServerExtras: false);
                break;

            case NodeType.Table: // SQL Server
                AddTableMenu(menu, node, canDesign: true, canDrop: true, sqlServerExtras: true);
                break;

            case NodeType.View when node.Connection.Engine is DatabaseEngine.Sqlite or DatabaseEngine.Firebird
                                     or DatabaseEngine.Oracle:
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                break;

            case NodeType.View when node.Connection.Engine.IsMySql():
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Drop", () => Run(Vm.DropRoutineCommand, node)));
                break;

            case NodeType.Function or NodeType.Procedure when node.Connection.Engine.IsMySql():
                menu.Items.Add(Item("Ctx_Drop", () => Run(Vm.DropRoutineCommand, node)));
                break;

            case NodeType.View:
                menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
                menu.Items.Add(Item("Ctx_Edit", () => Run(Vm.EditRoutineCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Drop", () => Run(Vm.DropRoutineCommand, node)));
                break;

            case NodeType.Function or NodeType.Procedure:
                menu.Items.Add(Item("Ctx_Edit", () => Run(Vm.EditRoutineCommand, node)));
                menu.Items.Add(Item("Ctx_Execute", () => Run(Vm.ExecuteRoutineCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Drop", () => Run(Vm.DropRoutineCommand, node)));
                break;

            case NodeType.Category when node.Connection.Engine == DatabaseEngine.Oracle:
                if (node.CategoryChildType is NodeType.Table) AddPaste(menu, node);
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.Connection.Engine == DatabaseEngine.Sqlite
                                         && node.CategoryChildType is NodeType.Table:
                menu.Items.Add(Item("Ctx_NewTable", () => Run(Vm.NewTableCommand, node)));
                AddPaste(menu, node);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.Connection.Engine is DatabaseEngine.Sqlite or DatabaseEngine.Firebird:
                if (node.CategoryChildType is NodeType.Table) AddPaste(menu, node);
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.Connection.Engine.IsMySql():
                if (node.CategoryChildType is NodeType.Table) AddPaste(menu, node);
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.CategoryChildType is NodeType.Function:
                menu.Items.Add(Item("Ctx_NewFunction", () => Run(Vm.NewRoutineCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.CategoryChildType is NodeType.Procedure:
                menu.Items.Add(Item("Ctx_NewProcedure", () => Run(Vm.NewRoutineCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.CategoryChildType is NodeType.View:
                menu.Items.Add(Item("Ctx_NewView", () => Run(Vm.NewRoutineCommand, node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Category when node.CategoryChildType is NodeType.Table:
                menu.Items.Add(Item("Ctx_NewTable", () => Run(Vm.NewTableCommand, node)));
                AddPaste(menu, node);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Database or NodeType.Schema:
                AddPaste(menu, node);
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            case NodeType.Server or NodeType.Category:
                menu.Items.Add(Item("Ctx_Refresh", () => Run(Vm.RefreshNodeCommand, node)));
                break;

            default:
                return null;
        }
        return menu;
    }

    private static void Run(System.Windows.Input.ICommand command, DbTreeNode node)
    {
        if (command.CanExecute(node)) command.Execute(node);
    }

    /// <summary>
    /// Builds a table's context menu to match the Objects list: Open, Design (where supported),
    /// Copy, Paste, Delete — plus SQL Server's Generate INSERT / Import extras when requested.
    /// </summary>
    private void AddTableMenu(ContextMenu menu, DbTreeNode node, bool canDesign, bool canDrop, bool sqlServerExtras)
    {
        MenuItem Item(string headerKey, Action action)
        {
            var mi = new MenuItem { Header = LocalizationManager.Instance[headerKey] };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(Item("Ctx_Open", () => Run(Vm.OpenTableCommand, node)));
        if (canDesign)
            menu.Items.Add(Item("Ctx_Design", () => Run(Vm.DesignTableCommand, node)));

        if (sqlServerExtras)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Ctx_GenerateInsert", () => Run(Vm.GenerateInsertsCommand, node)));
            menu.Items.Add(Item("Ctx_ImportData", () => Run(Vm.ImportDataCommand, node)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ctx_CopyTable", () => Run(Vm.CopyTableCommand, node)));
        // Paste is always offered (like the Objects list); it reports "nothing to paste" if the clipboard is empty.
        menu.Items.Add(Item("Ctx_PasteTable", () => Run(Vm.PasteTableCommand, node)));

        if (canDrop)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Ctx_Drop", () => Run(Vm.DropTableCommand, node)));
        }
    }

    /// <summary>Appends Copy (and Paste, when a table is on the clipboard) to a table's menu.</summary>
    private void AddCopyPaste(ContextMenu menu, DbTreeNode node)
    {
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = LocalizationManager.Instance["Ctx_CopyTable"] };
        copy.Click += (_, _) => Run(Vm.CopyTableCommand, node);
        menu.Items.Add(copy);
        AddPaste(menu, node);
    }

    /// <summary>Appends a Paste item only when a table has been copied.</summary>
    private void AddPaste(ContextMenu menu, DbTreeNode node)
    {
        if (!Vm.HasCopiedTable) return;
        var paste = new MenuItem { Header = LocalizationManager.Instance["Ctx_PasteTable"] };
        paste.Click += (_, _) => Run(Vm.PasteTableCommand, node);
        menu.Items.Add(paste);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }

    /// <summary>Collapse toolbar button labels to icons once the command bar gets tight.</summary>
    private void CommandHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TableTabViewModel tab })
            tab.IsToolbarCompact = e.NewSize.Width < 1000;
    }

    /// <summary>Drag handle above a docked pane resizes it (dragging up makes it taller).</summary>
    private void PaneThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.DataContext is not TableTabViewModel tab) return;

        static double Clamp(double v) => Math.Max(120, Math.Min(900, v));

        switch ((string)thumb.Tag)
        {
            case "sql":
                tab.SqlPaneHeight = Clamp(tab.SqlPaneHeight - e.VerticalChange);
                break;
            case "inspector":
                tab.InspectorWidth = Math.Max(220, Math.Min(1000, tab.InspectorWidth - e.HorizontalChange));
                break;
            default:
                tab.DetailPaneHeight = Clamp(tab.DetailPaneHeight - e.VerticalChange);
                break;
        }
    }
}
