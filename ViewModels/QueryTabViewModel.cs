using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Views;

namespace DataPortStudio.ViewModels;

/// <summary>A standalone SQL editor hosted in the main content tab strip.</summary>
public partial class QueryTabViewModel : ObservableObject, ITabItem
{
    public ConnectionProfile Connection { get; }
    public string? Database { get; }
    public string? InitialSql { get; }
    public string Header { get; }
    public bool CanClose => true;
    public string TabToolTip { get; }
    public QueryView View { get; }

    public event Action<QueryTabViewModel>? CloseRequested;

    public QueryTabViewModel(ConnectionProfile connection, string? database, string? initialSql = null)
    {
        Connection = connection;
        Database = database;
        InitialSql = initialSql;
        Header = "Query — " + (string.IsNullOrEmpty(database) ? connection.Name : database);
        TabToolTip = connection.Name + (string.IsNullOrEmpty(database) ? "" : " / " + database);
        View = new QueryView();
        View.Initialize(connection, database, initialSql);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this);
}
