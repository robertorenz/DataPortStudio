using System.Windows;
using DataPortStudio.Models;

namespace DataPortStudio.Views;

public partial class QueryWindow : Window
{
    public QueryWindow(ConnectionProfile connection, string? database, string? initialSql = null)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } window ? window : null;
        Title = $"Query — {connection.Name}" +
                (string.IsNullOrEmpty(database) ? "" : " / " + database);
        QueryContent.Initialize(connection, database, initialSql);
    }
}
