using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class QueryWindow : Window
{
    private readonly ConnectionProfile _connection;
    private readonly string? _database;
    private readonly string _historyKey;

    public QueryWindow(ConnectionProfile connection, string? database, string? initialSql = null)
    {
        InitializeComponent();
        _connection = connection;
        _database = database;
        _historyKey = $"{connection.Id}:{database}";
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        Title = $"Query — {connection.Name}" + (string.IsNullOrEmpty(database) ? "" : " / " + database);
        TargetLabel.Text = Title;

        SqlEditorHelper.Configure(Editor);
        SqlEditorHelper.ConfigureCompletion(Editor, connection, database);

        if (!string.IsNullOrEmpty(initialSql)) Editor.Document.Text = initialSql;

        PreviewKeyDown += async (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var ctrlShift = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);
            if (e.Key == Key.F5)                          { e.Handled = true; await RunAsync(); }
            if (e.Key == Key.E && ctrl)                   { e.Handled = true; await RunAsync(); }
            if (e.Key == Key.O && ctrl)                   { e.Handled = true; Load_Click(this, new()); }
            if (e.Key == Key.S && ctrl)                   { e.Handled = true; Save_Click(this, new()); }
            if (e.Key == Key.H && ctrl)                   { e.Handled = true; OpenHistory(); }
            if (e.Key == Key.F && ctrlShift)              { e.Handled = true; FormatSql(); }
        };
        Editor.Focus();
    }

    // ── Run ─────────────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ResultTabs.Items.Clear();
        ExportButton.IsEnabled = false;
        Messages.Text = "Cleared.";
    }

    private async Task RunAsync()
    {
        var selLen = Editor.SelectionLength;
        var sql = selLen > 0
            ? Editor.Document.GetText(Editor.SelectionStart, selLen)
            : Editor.Document.Text;
        if (string.IsNullOrWhiteSpace(sql)) return;

        RunButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        Messages.Text = "Running…";
        try
        {
            var tables   = new List<DataTable>();
            var truncated = new List<bool>();
            int affected  = -1;
            var sw = Stopwatch.StartNew();

            await using (var conn = CreateConnection())
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                try { cmd.CommandTimeout = 0; } catch { }
                await using var reader = await cmd.ExecuteReaderAsync();
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        var (tbl, trunc) = await ResultReader.LoadAsync(reader);
                        tables.Add(tbl);
                        truncated.Add(trunc);
                    }
                } while (await reader.NextResultAsync());
                affected = reader.RecordsAffected;
            }
            sw.Stop();

            ResultTabs.Items.Clear();
            if (tables.Count > 0)
            {
                for (int i = 0; i < tables.Count; i++)
                {
                    var t   = tables[i];
                    var trunc = truncated[i];
                    var header = tables.Count == 1
                        ? $"Results  ({t.Rows.Count:N0} rows)" + (trunc ? $"  · first {ResultReader.DefaultRowCap:N0}" : "")
                        : $"Result {i + 1}  ({t.Rows.Count:N0})" + (trunc ? " …" : "");

                    var grid = BuildGrid(t.DefaultView);
                    ResultTabs.Items.Add(new TabItem { Header = header, Content = grid });
                }
                ResultTabs.SelectedIndex = 0;
                ExportButton.IsEnabled = true;

                var msg = tables.Count == 1
                    ? $"{tables[0].Rows.Count:N0} row(s)  ·  {sw.ElapsedMilliseconds} ms"
                      + (truncated[0] ? $"  (first {ResultReader.DefaultRowCap:N0})" : "")
                    : $"{tables.Count} resultsets  ·  {tables.Sum(t => t.Rows.Count):N0} total rows  ·  {sw.ElapsedMilliseconds} ms";
                Messages.Text = msg;
            }
            else
            {
                Messages.Text = $"{(affected < 0 ? 0 : affected):N0} row(s) affected  ·  {sw.ElapsedMilliseconds} ms";
            }

            QueryHistoryStore.Add(_historyKey, sql);
        }
        catch (Exception ex)
        {
            ResultTabs.Items.Clear();
            ExportButton.IsEnabled = false;
            Messages.Text = "Error: " + ex.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private static DataGrid BuildGrid(DataView view)
    {
        var grid = new DataGrid
        {
            ItemsSource          = view,
            IsReadOnly           = true,
            AutoGenerateColumns  = true,
            CanUserAddRows       = false,
            BorderThickness      = new Thickness(0),
        };
        return grid;
    }

    // ── History ──────────────────────────────────────────────────────────────

    private void History_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void OpenHistory()
    {
        var entries = QueryHistoryStore.Get(_historyKey);
        if (entries.Count == 0) { Messages.Text = "No history for this connection yet."; return; }

        HistoryList.ItemsSource = entries
            .Select(s => new HistoryEntry(s, MakePreview(s)))
            .ToList();
        HistoryPopup.IsOpen = true;
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry)
        {
            Editor.Document.Text = entry.Sql;
            HistoryPopup.IsOpen  = false;
            HistoryList.SelectedItem = null;
        }
    }

    private async void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry)
        {
            Editor.Document.Text = entry.Sql;
            HistoryPopup.IsOpen  = false;
            HistoryList.SelectedItem = null;
            await RunAsync();
        }
    }

    private static string MakePreview(string sql)
    {
        var first = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .FirstOrDefault(l => !l.TrimStart().StartsWith("--"))
                       ?.Trim() ?? sql;
        return first.Length > 80 ? first[..77] + "…" : first;
    }

    private record HistoryEntry(string Sql, string Preview);

    // ── Format ───────────────────────────────────────────────────────────────

    private void Format_Click(object sender, RoutedEventArgs e) => FormatSql();

    private void FormatSql()
    {
        var selLen = Editor.SelectionLength;
        if (selLen > 0)
        {
            var selStart = Editor.SelectionStart;
            var formatted = SqlBeautifier.Format(Editor.Document.GetText(selStart, selLen));
            Editor.Document.Replace(selStart, selLen, formatted);
        }
        else
        {
            var caret = Editor.CaretOffset;
            Editor.Document.Text = SqlBeautifier.Format(Editor.Document.Text);
            Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        }
    }

    // ── Load / Save script ──────────────────────────────────────────────────

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load SQL script",
            Filter = "SQL files (*.sql)|*.sql|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".sql"
        };
        if (dlg.ShowDialog(this) == true)
            Editor.Document.Text = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save SQL script",
            Filter = "SQL files (*.sql)|*.sql|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".sql",
            FileName = "query"
        };
        if (dlg.ShowDialog(this) == true)
            System.IO.File.WriteAllText(dlg.FileName, Editor.Document.Text, System.Text.Encoding.UTF8);
    }

    // ── Export ───────────────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ResultTabs.SelectedItem is not TabItem tab) return;
        if (tab.Content is not DataGrid grid) return;
        if (grid.ItemsSource is not DataView view) return;
        var dlg = new ExportDialog(view, _database ?? _connection.Name) { Owner = this };
        dlg.ShowDialog();
    }

    // ── Connection factory ───────────────────────────────────────────────────

    private DbConnection CreateConnection()
    {
        var cs = _connection.BuildConnectionString();
        return _connection.Engine switch
        {
            DatabaseEngine.Sqlite    => new SqliteConnection(cs),
            DatabaseEngine.Firebird  => new FbConnection(cs),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(_database) ? cs : MySqlService.WithDatabase(cs, _database)),
            DatabaseEngine.Oracle    => new Oracle.ManagedDataAccess.Client.OracleConnection(cs),
            DatabaseEngine.PostgreSql =>
                new Npgsql.NpgsqlConnection(string.IsNullOrEmpty(_database) ? cs : PostgresService.WithDatabase(cs, _database)),
            _                        => new SqlConnection(string.IsNullOrEmpty(_database) ? cs : SqlServerService.WithDatabase(cs, _database))
        };
    }
}
