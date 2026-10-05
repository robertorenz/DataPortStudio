using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>
/// Compares the rows of two tables (any two relational connections) and makes the target match
/// the source: a sync script to review, or the changes applied in one transaction.
/// </summary>
public partial class DataCompareWindow : Window
{
    private sealed record ConnectionChoice(string Label, ConnectionProfile Connection);

    /// <summary>The four pickers of one endpoint.</summary>
    private sealed record Side(ComboBox Conn, ComboBox Db, ComboBox Schema, ComboBox Table);

    private const int GridRowCap = 5_000;

    private readonly List<ConnectionChoice> _choices;
    private readonly Side _src;
    private readonly Side _tgt;
    private readonly ConnectionProfile _initialConnection;
    private readonly string? _initialDatabase;
    private readonly string? _initialSchema;
    private readonly string? _initialTable;
    private DataCompareResult? _result;
    private CancellationTokenSource? _cts;
    private bool _loading;

    private static string L(string key) => LocalizationManager.Instance[key];

    public DataCompareWindow(IEnumerable<ConnectionProfile> connections, ConnectionProfile initialConnection,
        string? database = null, string? schema = null, string? table = null)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        _choices = connections.Where(DataCompareService.IsSupported).DistinctBy(c => c.Id)
            .Select(c => new ConnectionChoice($"{c.Name} ({c.Engine.DisplayName()})", c)).ToList();
        _src = new Side(SrcConnCombo, SrcDbCombo, SrcSchemaCombo, SrcTableCombo);
        _tgt = new Side(TgtConnCombo, TgtDbCombo, TgtSchemaCombo, TgtTableCombo);
        _initialConnection = initialConnection;
        _initialDatabase = database;
        _initialSchema = schema;
        _initialTable = table;

        foreach (var grid in new[] { InsertGrid, UpdateGrid, DeleteGrid })
            grid.AutoGeneratingColumn += Grid_AutoGeneratingColumn;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    // ── Endpoints ───────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            SrcConnCombo.ItemsSource = _choices;
            TgtConnCombo.ItemsSource = _choices;
            SrcConnCombo.SelectedItem = _choices.FirstOrDefault(c => c.Connection.Id == _initialConnection.Id)
                                        ?? _choices.FirstOrDefault();
            TgtConnCombo.SelectedItem = SrcConnCombo.SelectedItem;
            await LoadDatabasesAsync(_src, _initialDatabase, _initialSchema, _initialTable);
            await LoadDatabasesAsync(_tgt, null, null, _initialTable);
            // Same connection: a different database is the likelier target.
            if (Equals(SrcDbCombo.SelectedItem, TgtDbCombo.SelectedItem) && TgtDbCombo.Items.Count > 1)
            {
                TgtDbCombo.SelectedIndex = SrcDbCombo.SelectedIndex == 0 ? 1 : 0;
                await LoadSchemasAsync(_tgt, null, _initialTable);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L("DC_LoadError") + " " + ex.Message;
        }
        finally
        {
            _loading = false;
            UpdateState();
        }
    }

    private Side SideOf(object sender) =>
        sender == SrcConnCombo || sender == SrcDbCombo || sender == SrcSchemaCombo || sender == SrcTableCombo ? _src : _tgt;

    private async void Conn_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        await ReloadAsync(() => LoadDatabasesAsync(SideOf(sender), null, null, TableName(_src)));
    }

    private async void Db_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        await ReloadAsync(() => LoadSchemasAsync(SideOf(sender), null, TableName(SideOf(sender))));
    }

    private async void Schema_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        await ReloadAsync(() => LoadTablesAsync(SideOf(sender), TableName(SideOf(sender))));
    }

    private void Table_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        // Picking a source table follows through to a same-named target table, when there is one.
        if (sender == SrcTableCombo && e.AddedItems.Count > 0 && e.AddedItems[0] is string name &&
            TgtTableCombo.ItemsSource is IEnumerable<string> targets &&
            targets.FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match)
            TgtTableCombo.SelectedItem = match;
        ClearResult();
        UpdateState();
    }

    private async Task ReloadAsync(Func<Task> load)
    {
        _loading = true;
        try { await load(); }
        catch (Exception ex) { StatusText.Text = L("DC_LoadError") + " " + ex.Message; }
        finally { _loading = false; ClearResult(); UpdateState(); }
    }

    private async Task LoadDatabasesAsync(Side side, string? db, string? schema, string? table)
    {
        var conn = (side.Conn.SelectedItem as ConnectionChoice)?.Connection;
        side.Db.ItemsSource = null;
        if (conn is null) return;
        StatusText.Text = string.Format(L("DC_LoadingFrom"), conn.Name);
        var dbs = await SchemaDiffService.GetDatabasesAsync(conn);
        side.Db.ItemsSource = dbs;
        side.Db.SelectedItem = dbs.FirstOrDefault(d => d.Equals(db ?? conn.Database, StringComparison.OrdinalIgnoreCase))
                               ?? dbs.FirstOrDefault();
        await LoadSchemasAsync(side, schema, table);
        StatusText.Text = L("DC_StatusReady");
    }

    private async Task LoadSchemasAsync(Side side, string? schema, string? table)
    {
        var conn = (side.Conn.SelectedItem as ConnectionChoice)?.Connection;
        side.Schema.ItemsSource = null;
        if (conn is null || side.Db.SelectedItem is not string db) return;
        var schemas = await SchemaDiffService.GetSchemasAsync(conn, db);
        side.Schema.ItemsSource = schemas;
        var preferred = schema ?? conn.Engine switch
        {
            DatabaseEngine.SqlServer => "dbo",
            DatabaseEngine.PostgreSql => "public",
            _ => null
        };
        side.Schema.SelectedItem = schemas.FirstOrDefault(s => s.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                                   ?? schemas.FirstOrDefault();
        // Single-schema engines (Firebird's is "") hide the picker's emptiness behind a disabled box.
        side.Schema.IsEnabled = schemas.Count > 1;
        await LoadTablesAsync(side, table);
    }

    private async Task LoadTablesAsync(Side side, string? table)
    {
        var conn = (side.Conn.SelectedItem as ConnectionChoice)?.Connection;
        side.Table.ItemsSource = null;
        if (conn is null || side.Db.SelectedItem is not string db || side.Schema.SelectedItem is not string schema) return;
        var tables = (await TableCopyService.ListObjectsAsync(conn, db, schema))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        side.Table.ItemsSource = tables;
        side.Table.SelectedItem = tables.FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TableName(Side side) => side.Table.SelectedItem as string;

    private static DataEndpoint? Endpoint(Side side) =>
        side.Conn.SelectedItem is ConnectionChoice c && side.Db.SelectedItem is string db &&
        side.Schema.SelectedItem is string schema && side.Table.SelectedItem is string table
            ? new DataEndpoint(new SchemaEndpoint(c.Connection, db, schema), table)
            : null;

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        var src = Endpoint(_src);
        var tgt = Endpoint(_tgt);
        _loading = true;
        try
        {
            SrcConnCombo.SelectedItem = _choices.FirstOrDefault(c => c.Connection.Id == tgt?.Connection.Id) ?? TgtConnCombo.SelectedItem;
            TgtConnCombo.SelectedItem = _choices.FirstOrDefault(c => c.Connection.Id == src?.Connection.Id) ?? SrcConnCombo.SelectedItem;
            await LoadDatabasesAsync(_src, tgt?.Location.Database, tgt?.Location.Schema, tgt?.Table);
            await LoadDatabasesAsync(_tgt, src?.Location.Database, src?.Location.Schema, src?.Table);
        }
        catch (Exception ex) { StatusText.Text = L("DC_LoadError") + " " + ex.Message; }
        finally { _loading = false; ClearResult(); UpdateState(); }
    }

    private void UpdateState()
    {
        var src = Endpoint(_src);
        var tgt = Endpoint(_tgt);
        var same = src is not null && tgt is not null &&
                   src.Connection.Id == tgt.Connection.Id &&
                   string.Equals(src.Location.Database, tgt.Location.Database, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(src.Location.Schema, tgt.Location.Schema, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(src.Table, tgt.Table, StringComparison.OrdinalIgnoreCase);
        CompareButton.IsEnabled = _cts is not null || (src is not null && tgt is not null && !same);

        var pending = _result is null ? 0 : DataCompareService.Count(_result, SyncOptions());
        ScriptButton.IsEnabled = _cts is null && pending > 0;
        ApplyButton.IsEnabled = _cts is null && pending > 0;
    }

    private DataSyncOptions SyncOptions() =>
        new(DoInsertCheck.IsChecked == true, DoUpdateCheck.IsChecked == true, DoDeleteCheck.IsChecked == true);

    private void SyncOption_Click(object sender, RoutedEventArgs e) => UpdateState();

    // ── Compare ─────────────────────────────────────────────────────────────

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        await CompareAsync();
    }

    private async Task CompareAsync()
    {
        var src = Endpoint(_src);
        var tgt = Endpoint(_tgt);
        if (src is null || tgt is null) return;

        ClearResult();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        CompareButton.Content = L("Btn_Cancel");
        var sw = Stopwatch.StartNew();
        try
        {
            var options = new DataCompareOptions
            {
                IgnoreTrailingSpaces = TrimCheck.IsChecked == true,
                IgnoreCase = CaseCheck.IsChecked == true,
            };
            var progress = new Progress<string>(s => StatusText.Text = s);
            _result = await Task.Run(() => DataCompareService.CompareAsync(src, tgt, options, progress, _cts.Token));
            ShowResult(_result);
            StatusText.Text = (_result.IsInSync
                ? string.Format(L("DC_InSync"), _result.Identical)
                : string.Format(L("DC_Summary"), _result.OnlyInSource.Count, _result.Different.Count,
                    _result.OnlyInTarget.Count, _result.Identical)) + $"  ·  {sw.ElapsedMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("DC_Cancelled");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("DC_Failed") + " " + ex.Message;
            Dialogs.ShowError(L("DC_Title"), ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CompareButton.Content = L("DC_Compare");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in new[] { _src, _tgt }.SelectMany(s => new[] { s.Conn, s.Db, s.Schema, s.Table }))
            c.IsEnabled = !busy;
        if (!busy)
        {
            SrcSchemaCombo.IsEnabled = SrcSchemaCombo.Items.Count > 1;
            TgtSchemaCombo.IsEnabled = TgtSchemaCombo.Items.Count > 1;
        }
        SwapButton.IsEnabled = !busy;
        TrimCheck.IsEnabled = !busy;
        CaseCheck.IsEnabled = !busy;
        Cursor = busy ? Cursors.AppStarting : null;
        UpdateState();
    }

    private void ClearResult()
    {
        _result = null;
        InsertGrid.ItemsSource = UpdateGrid.ItemsSource = DeleteGrid.ItemsSource = null;
        InsertCount.Text = UpdateCount.Text = DeleteCount.Text = SameCount.Text = "—";
        InsertTab.Header = L("DC_TabOnlySource");
        UpdateTab.Header = L("DC_TabDifferent");
        DeleteTab.Header = L("DC_TabOnlyTarget");
        KeyText.Text = "";
    }

    private void ShowResult(DataCompareResult r)
    {
        InsertCount.Text = r.OnlyInSource.Count.ToString("N0");
        UpdateCount.Text = r.Different.Count.ToString("N0");
        DeleteCount.Text = r.OnlyInTarget.Count.ToString("N0");
        SameCount.Text = r.Identical.ToString("N0");
        InsertTab.Header = TabHeader("DC_TabOnlySource", r.OnlyInSource.Count);
        UpdateTab.Header = TabHeader("DC_TabDifferent", r.Different.Count);
        DeleteTab.Header = TabHeader("DC_TabOnlyTarget", r.OnlyInTarget.Count);

        KeyText.Text = string.Format(L("DC_KeyInfo"), string.Join(", ", r.KeyIndexes.Select(i => r.Columns[i])), r.Columns.Count)
                       + (r.SkippedColumns.Count > 0
                           ? "  ·  " + string.Format(L("DC_Skipped"), string.Join(", ", r.SkippedColumns))
                           : "");

        InsertGrid.ItemsSource = RowsTable(r, r.OnlyInSource).DefaultView;
        DeleteGrid.ItemsSource = RowsTable(r, r.OnlyInTarget).DefaultView;
        UpdateGrid.ItemsSource = ChangesTable(r).DefaultView;

        DetailTabs.SelectedItem = r.Different.Count > 0 ? UpdateTab
            : r.OnlyInSource.Count > 0 ? InsertTab
            : r.OnlyInTarget.Count > 0 ? DeleteTab
            : DetailTabs.SelectedItem;
    }

    private static string TabHeader(string key, int count) =>
        $"{L(key)}  ({count:N0})" + (count > GridRowCap ? "  · " + string.Format(L("DC_FirstRows"), GridRowCap) : "");

    /// <summary>Whole rows. Columns are named c0, c1… (captions carry the real names) so any name binds.</summary>
    private static DataTable RowsTable(DataCompareResult r, List<object?[]> rows)
    {
        var t = new DataTable();
        for (var i = 0; i < r.Columns.Count; i++) t.Columns.Add(new DataColumn("c" + i) { Caption = r.Columns[i] });
        foreach (var row in rows.Take(GridRowCap))
            t.Rows.Add(row.Select(Display).Cast<object>().ToArray());
        return t;
    }

    /// <summary>One line per changed cell: the row's key, the column, the target's value and the source's.</summary>
    private static DataTable ChangesTable(DataCompareResult r)
    {
        var t = new DataTable();
        var k = 0;
        foreach (var i in r.KeyIndexes) t.Columns.Add(new DataColumn("c" + k++) { Caption = r.Columns[i] });
        t.Columns.Add(new DataColumn("col") { Caption = L("DC_Column") });
        t.Columns.Add(new DataColumn("tgt") { Caption = L("DC_TargetValue") });
        t.Columns.Add(new DataColumn("src") { Caption = L("DC_SourceValue") });
        var lines = 0;
        foreach (var d in r.Different)
        {
            foreach (var c in d.Changed)
            {
                var values = r.KeyIndexes.Select(i => Display(d.Source[i]))
                    .Append(r.Columns[c]).Append(Display(d.Target[c])).Append(Display(d.Source[c]));
                t.Rows.Add(values.Cast<object>().ToArray());
                if (++lines >= GridRowCap) return t;
            }
        }
        return t;
    }

    private static string Display(object? v) => v switch
    {
        null or DBNull => "(NULL)",
        byte[] b => "0x" + Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 32))) + (b.Length > 32 ? "…" : ""),
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => v.ToString() ?? "",
    };

    private static void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: DataView view } && view.Table!.Columns[e.PropertyName] is { } col)
            e.Column.Header = col.Caption;
    }

    private void InsertCard_Click(object sender, MouseButtonEventArgs e) => DetailTabs.SelectedItem = InsertTab;
    private void UpdateCard_Click(object sender, MouseButtonEventArgs e) => DetailTabs.SelectedItem = UpdateTab;
    private void DeleteCard_Click(object sender, MouseButtonEventArgs e) => DetailTabs.SelectedItem = DeleteTab;

    // ── Sync ────────────────────────────────────────────────────────────────

    private void Script_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        try
        {
            var script = DataCompareService.BuildScript(_result, SyncOptions());
            new ScriptViewerWindow(string.Format(L("DC_ScriptTitle"), _result.Target.Table), script,
                $"{_result.Target.Table}_sync").Show();
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(L("DC_Title"), ex.Message);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var r = _result;
        if (r is null) return;
        var sync = SyncOptions();
        var message = string.Format(L("DC_ApplyConfirm"), r.Target.DisplayName,
            sync.Insert ? r.OnlyInSource.Count : 0,
            sync.Update ? r.Different.Count : 0,
            sync.Delete ? r.OnlyInTarget.Count : 0);
        if (!Dialogs.ConfirmDanger(L("DC_ApplyTitle"), message, L("DC_Apply"))) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        CompareButton.Content = L("Btn_Cancel");
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            var done = await Task.Run(() => DataCompareService.ApplyAsync(r, sync, progress, _cts.Token));
            Dialogs.ShowSuccess(L("DC_ApplyTitle"), string.Format(L("DC_Applied"), done, r.Target.DisplayName));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("DC_ApplyCancelled");
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("DC_Failed") + " " + ex.Message;
            Dialogs.ShowError(L("DC_ApplyTitle"), L("DC_ApplyRolledBack") + "\n\n" + ex.Message);
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CompareButton.Content = L("DC_Compare");
            SetBusy(false);
        }
        // Show the tables as they now stand.
        await CompareAsync();
    }
}
