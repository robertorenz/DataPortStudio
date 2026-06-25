using System.Windows;
using DataPortStudio.Models;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class TableDesignerWindow : Window
{
    public TableDesignerWindow(ConnectionProfile connection, string? database, string schema, string table, bool isNew)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        Title = isNew ? "New Table" : $"Design Table — {schema}.{table}";
        DataContext = new TableDesignerViewModel(connection, database, schema, table, isNew);
    }
}
