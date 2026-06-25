using System.Windows;
using DataPortStudio.Models;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class QueryBuilderWindow : Window
{
    public QueryBuilderWindow(ConnectionProfile connection, string? database)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        Title = $"Query Builder — {connection.Name}" + (string.IsNullOrEmpty(database) ? "" : " / " + database);
        DataContext = new QueryBuilderViewModel(connection, database);
    }
}
