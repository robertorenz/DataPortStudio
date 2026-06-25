using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Data.Common;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.ViewModels;

public partial class QueryBuilderViewModel : ObservableObject
{
    private readonly ConnectionProfile _connection;
    private readonly string? _database;
    private DatabaseEngine Engine => _connection.Engine;

    public ObservableCollection<string> AllTables { get; } = new();          // "schema.table"
    public ObservableCollection<BuilderTable> Tables { get; } = new();
    public ObservableCollection<JoinRow> Joins { get; } = new();
    public ObservableCollection<FilterRow> Filters { get; } = new();
    public ObservableCollection<BuilderSort> Sorts { get; } = new();
    public ObservableCollection<string> AvailableColumns { get; } = new();   // "[table].[col]"

    [ObservableProperty] private string? selectedTableToAdd;
    [ObservableProperty] private string generatedSql = "";
    [ObservableProperty] private DataView? results;
    [ObservableProperty] private string messages = "Add a table to begin.";

    public Array JoinTypes { get; } = Enum.GetValues(typeof(JoinType));
    public Array FilterOperatorValues { get; } = Enum.GetValues(typeof(FilterOperator));
    public Array SortDirections { get; } = Enum.GetValues(typeof(SortDirection));
    public Array Connectors { get; } = Enum.GetValues(typeof(BoolConnector));

    public QueryBuilderViewModel(ConnectionProfile connection, string? database)
    {
        _connection = connection;
        _database = database;

        WireRowCollection(Joins);
        WireRowCollection(Filters);
        WireRowCollection(Sorts);
        Tables.CollectionChanged += (_, _) => { RebuildAvailable(); Regenerate(); };

        _ = LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        try
        {
            var cs = _connection.BuildConnectionString();
            AllTables.Clear();
            switch (Engine)
            {
                case DatabaseEngine.Sqlite:
                    foreach (var t in await SqliteService.GetTablesAsync(cs)) AllTables.Add(t);
                    break;
                case DatabaseEngine.Firebird:
                    foreach (var t in await FirebirdService.GetTablesAsync(cs)) AllTables.Add(t);
                    break;
                case DatabaseEngine.MySql:
                case DatabaseEngine.MariaDb:
                    foreach (var t in await MySqlService.GetTablesAsync(cs, _database ?? "")) AllTables.Add(t);
                    break;
                default:
                    foreach (var (s, t) in await SqlServerService.GetAllTablesAsync(cs, _database ?? ""))
                        AllTables.Add($"{s}.{t}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Messages = "Could not load tables: " + ex.Message;
        }
    }

    private async Task<List<string>> GetColumnsAsync(string schema, string table)
    {
        var cs = _connection.BuildConnectionString();
        return Engine switch
        {
            DatabaseEngine.Sqlite => await SqliteService.GetColumnNamesAsync(cs, table),
            DatabaseEngine.Firebird => await FirebirdService.GetColumnNamesAsync(cs, table),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => await MySqlService.GetColumnNamesAsync(cs, _database ?? "", table),
            _ => await SqlServerService.GetColumnNamesAsync(cs, _database ?? "", schema, table)
        };
    }

    private DbConnection CreateConnection()
    {
        var cs = _connection.BuildConnectionString();
        return Engine switch
        {
            DatabaseEngine.Sqlite => new SqliteConnection(cs),
            DatabaseEngine.Firebird => new FbConnection(cs),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(_database) ? cs : MySqlService.WithDatabase(cs, _database)),
            _ => new SqlConnection(string.IsNullOrEmpty(_database) ? cs : SqlServerService.WithDatabase(cs, _database))
        };
    }

    // ---- tables ----------------------------------------------------------

    [RelayCommand]
    private async Task AddTable()
    {
        if (string.IsNullOrEmpty(SelectedTableToAdd)) return;
        string schema, table;
        var dot = SelectedTableToAdd.IndexOf('.');
        if (Engine == DatabaseEngine.SqlServer && dot > 0)
        {
            schema = SelectedTableToAdd[..dot];
            table = SelectedTableToAdd[(dot + 1)..];
        }
        else { schema = ""; table = SelectedTableToAdd; }

        var bt = new BuilderTable { Engine = Engine, Schema = schema, Table = table };
        try
        {
            var cols = await GetColumnsAsync(schema, table);
            foreach (var c in cols)
            {
                var bc = new BuilderColumn { Engine = Engine, Table = table, Name = c };
                bc.PropertyChanged += OnRowChanged;
                bt.Columns.Add(bc);
            }
        }
        catch (Exception ex)
        {
            Messages = "Could not load columns: " + ex.Message;
            return;
        }
        Tables.Add(bt);
    }

    [RelayCommand]
    private void RemoveTable(BuilderTable? t)
    {
        if (t is null) return;
        foreach (var c in t.Columns) c.PropertyChanged -= OnRowChanged;
        Tables.Remove(t);
    }

    // ---- joins / filters / sorts ----------------------------------------

    [RelayCommand] private void AddJoin() => Joins.Add(new JoinRow());
    [RelayCommand] private void RemoveJoin(JoinRow? j) { if (j is not null) Joins.Remove(j); }

    [RelayCommand] private void AddFilter() => Filters.Add(new FilterRow());
    [RelayCommand] private void RemoveFilter(FilterRow? f) { if (f is not null) Filters.Remove(f); }

    [RelayCommand] private void AddSort() => Sorts.Add(new BuilderSort());
    [RelayCommand] private void RemoveSort(BuilderSort? s) { if (s is not null) Sorts.Remove(s); }

    [RelayCommand]
    private async Task DetectJoins()
    {
        try
        {
            var cs = _connection.BuildConnectionString();
            var present = new HashSet<string>(Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
            var added = 0;

            void TryAdd(string lt, string lc, string rt, string rc)
            {
                if (!present.Contains(lt) || !present.Contains(rt)) return;
                var left = Qb.Col(Engine, lt, lc);
                var right = Qb.Col(Engine, rt, rc);
                if (Joins.Any(j => j.LeftColumn == left && j.RightColumn == right)) return;
                Joins.Add(new JoinRow { LeftColumn = left, JoinType = JoinType.Inner, RightColumn = right });
                added++;
            }

            if (Engine == DatabaseEngine.SqlServer)
            {
                foreach (var (ps, pt, pc, rs, rt, rc) in await SqlServerService.GetAllForeignKeysAsync(cs, _database ?? ""))
                    TryAdd(pt, pc, rt, rc);
            }
            else if (Engine == DatabaseEngine.Firebird)
            {
                foreach (var bt in Tables.ToList())
                    foreach (var fk in await FirebirdService.GetForeignKeysAsync(cs, bt.Table))
                        for (var i = 0; i < fk.Cols.Count && i < fk.RefCols.Count; i++)
                            TryAdd(bt.Table, fk.Cols[i], fk.RefTable, fk.RefCols[i]);
            }
            else if (Engine == DatabaseEngine.Sqlite)
            {
                foreach (var bt in Tables.ToList())
                    foreach (var fk in await SqliteService.GetForeignKeysAsync(cs, bt.Table))
                        for (var i = 0; i < fk.Cols.Count && i < fk.RefCols.Count; i++)
                            TryAdd(bt.Table, fk.Cols[i], fk.RefTable, fk.RefCols[i]);
            }
            else if (Engine.IsMySql())
            {
                foreach (var bt in Tables.ToList())
                    foreach (var fk in await MySqlService.GetForeignKeysAsync(cs, _database ?? "", bt.Table))
                        for (var i = 0; i < fk.Cols.Count && i < fk.RefCols.Count; i++)
                            TryAdd(bt.Table, fk.Cols[i], fk.RefTable, fk.RefCols[i]);
            }

            Messages = added > 0 ? $"Added {added} join(s) from foreign keys." : "No foreign keys found between the selected tables.";
        }
        catch (Exception ex)
        {
            Messages = "Detect joins failed: " + ex.Message;
        }
    }

    // ---- run -------------------------------------------------------------

    [RelayCommand]
    private async Task Run()
    {
        if (string.IsNullOrWhiteSpace(GeneratedSql)) return;
        Messages = "Running…";
        try
        {
            System.Data.DataTable data;
            bool truncated;
            await using (var conn = CreateConnection())
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = GeneratedSql;
                try { cmd.CommandTimeout = 0; } catch { /* provider may not allow 0 */ }
                await using var reader = await cmd.ExecuteReaderAsync();
                (data, truncated) = await ResultReader.LoadAsync(reader);
            }
            Results = data.DefaultView;
            Messages = $"{data.Rows.Count:N0} row(s)." +
                       (truncated ? $"  (showing the first {ResultReader.DefaultRowCap:N0})" : "");
        }
        catch (Exception ex)
        {
            Results = null;
            Messages = "Error: " + ex.Message;
        }
    }

    // ---- generation ------------------------------------------------------

    private void RebuildAvailable()
    {
        AvailableColumns.Clear();
        foreach (var t in Tables)
            foreach (var c in t.Columns)
                AvailableColumns.Add(c.Reference);
    }

    private void Regenerate()
    {
        if (Tables.Count == 0) { GeneratedSql = ""; return; }

        var included = Tables.SelectMany(t => t.Columns).Where(c => c.Included).Select(c => c.Reference).ToList();
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(included.Count > 0 ? string.Join(", ", included) : "*").Append('\n');
        sb.Append("FROM ").Append(Tables[0].FromClause);

        var inFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Tables[0].Table };
        foreach (var j in Joins)
        {
            if (string.IsNullOrEmpty(j.LeftColumn) || string.IsNullOrEmpty(j.RightColumn)) continue;
            var rightTable = TableOf(j.RightColumn);
            var bt = Tables.FirstOrDefault(t => t.Table.Equals(rightTable, StringComparison.OrdinalIgnoreCase));
            if (bt is null) continue;
            sb.Append('\n').Append(JoinKeyword(j.JoinType)).Append(' ').Append(bt.FromClause)
              .Append(" ON ").Append(j.LeftColumn).Append(" = ").Append(j.RightColumn);
            inFrom.Add(bt.Table);
        }
        foreach (var t in Tables.Skip(1))
            if (!inFrom.Contains(t.Table)) { sb.Append("\n, ").Append(t.FromClause); inFrom.Add(t.Table); }

        var fs = Filters.Where(f => !string.IsNullOrEmpty(f.Column)).ToList();
        if (fs.Count > 0)
        {
            sb.Append("\nWHERE ");
            for (var i = 0; i < fs.Count; i++)
            {
                if (i > 0) sb.Append(' ').Append(fs[i].Connector == BoolConnector.Or ? "OR" : "AND").Append(' ');
                sb.Append(FilterClause(fs[i]));
            }
        }

        var ss = Sorts.Where(s => !string.IsNullOrEmpty(s.Column)).ToList();
        if (ss.Count > 0)
            sb.Append("\nORDER BY ")
              .Append(string.Join(", ", ss.Select(s => $"{s.Column} {(s.Direction == SortDirection.Desc ? "DESC" : "ASC")}")));

        GeneratedSql = sb.ToString();
    }

    private static string FilterClause(FilterRow f)
    {
        var col = f.Column!;
        var v = f.Value ?? "";
        string Lit(string x) => double.TryParse(x, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            ? x : "'" + x.Replace("'", "''") + "'";
        string Like(string p) => $"{col} LIKE '{p.Replace("'", "''")}'";

        return f.Operator switch
        {
            FilterOperator.Contains => Like($"%{v}%"),
            FilterOperator.StartsWith => Like($"{v}%"),
            FilterOperator.EndsWith => Like($"%{v}"),
            FilterOperator.Equals => $"{col} = {Lit(v)}",
            FilterOperator.NotEquals => $"{col} <> {Lit(v)}",
            FilterOperator.GreaterThan => $"{col} > {Lit(v)}",
            FilterOperator.LessThan => $"{col} < {Lit(v)}",
            FilterOperator.GreaterOrEqual => $"{col} >= {Lit(v)}",
            FilterOperator.LessOrEqual => $"{col} <= {Lit(v)}",
            FilterOperator.IsEmpty => $"{col} IS NULL",
            FilterOperator.IsNotEmpty => $"{col} IS NOT NULL",
            _ => ""
        };
    }

    private string TableOf(string columnRef)
    {
        var open = Engine == DatabaseEngine.Firebird ? '"' : Engine.IsMySql() ? '`' : '[';
        var close = Engine == DatabaseEngine.Firebird ? '"' : Engine.IsMySql() ? '`' : ']';
        var start = columnRef.IndexOf(open);
        var end = columnRef.IndexOf(close, start + 1);
        return start >= 0 && end > start ? columnRef[(start + 1)..end] : "";
    }

    private static string JoinKeyword(JoinType t) => t switch
    {
        JoinType.Left => "LEFT JOIN",
        JoinType.Right => "RIGHT JOIN",
        JoinType.Full => "FULL JOIN",
        _ => "INNER JOIN"
    };

    // ---- live-update wiring ---------------------------------------------

    private void WireRowCollection(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (INotifyPropertyChanged i in e.NewItems) i.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null)
                foreach (INotifyPropertyChanged i in e.OldItems) i.PropertyChanged -= OnRowChanged;
            Regenerate();
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Regenerate();
}
