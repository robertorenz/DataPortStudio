using System.Windows;
using DataPortStudio.Models;
using DataPortStudio.Services;
using Microsoft.Web.WebView2.Core;

namespace DataPortStudio.Views;

public partial class ErDiagramWindow : Window
{
    private readonly ConnectionProfile _connection;
    private readonly string? _database;
    private readonly string _schema;

    public ErDiagramWindow(ConnectionProfile connection, string? database, string schema = "dbo")
    {
        InitializeComponent();
        _connection = connection;
        _database   = database;
        _schema     = schema;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        var label = string.IsNullOrEmpty(database) ? connection.Name : $"{connection.Name} / {database}";
        Title       = $"ER Diagram — {label}";
        TitleLabel.Text = Title;

        Loaded += async (_, _) => await LoadDiagramAsync();
    }

    private async Task LoadDiagramAsync()
    {
        StatusText.Text = "Loading schema…";
        try
        {
            await WebView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
            var (tables, fks) = await ErDiagramService.LoadAsync(_connection, _database, _schema);

            StatusText.Text = $"{tables.Count} tables · {fks.Count} FK relationships";
            var html = ErDiagramService.BuildHtml(tables, fks);
            WebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDiagramAsync();

    private void Relayout_Click(object sender, RoutedEventArgs e)
    {
        // Trigger the JS layout function in the loaded page
        _ = WebView.CoreWebView2?.ExecuteScriptAsync("layout(200);draw();");
    }
}
