using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.ViewModels;

public partial class TableDesignerViewModel : ObservableObject
{
    private readonly ConnectionProfile _connection;
    private readonly DatabaseEngine _engine;
    private readonly string? _database;
    private string _schema;
    private string _table;
    private readonly bool _isNew;
    private List<string> _originalColumns = new();
    private string? _pkName;
    private List<string> _originalPk = new();
    private List<string> _originalIndexNames = new();

    private bool IsSqlite => _engine == DatabaseEngine.Sqlite;

    public ObservableCollection<DesignColumn> Columns { get; } = new();
    public ObservableCollection<DesignIndex> Indexes { get; } = new();

    private static readonly string[] SqlServerTypes =
    {
        "int", "bigint", "smallint", "tinyint", "bit",
        "decimal", "numeric", "money", "float", "real",
        "date", "datetime", "datetime2", "time", "datetimeoffset",
        "char", "varchar", "nchar", "nvarchar", "text", "ntext",
        "uniqueidentifier", "varbinary", "binary", "xml"
    };

    private static readonly string[] SqliteTypes =
    {
        "INTEGER", "TEXT", "REAL", "NUMERIC", "BLOB", "BOOLEAN", "DATE", "DATETIME"
    };

    public string[] DataTypes { get; }

    [ObservableProperty] private string tableName;
    [ObservableProperty] private string generatedSql = "";
    [ObservableProperty] private string messages = "";

    public TableDesignerViewModel(ConnectionProfile connection, string? database, string schema, string table, bool isNew)
    {
        _connection = connection;
        _engine = connection.Engine;
        _database = database;
        _schema = schema;
        _table = table;
        _isNew = isNew;
        tableName = table;
        DataTypes = _engine == DatabaseEngine.Sqlite ? SqliteTypes : SqlServerTypes;

        Columns.CollectionChanged += OnColumnsChanged;
        Indexes.CollectionChanged += OnColumnsChanged;

        if (_isNew)
        {
            AddColumn();
            Generate();
        }
        else
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (IsSqlite) { await LoadSqliteAsync(); return; }
        try
        {
            var cols = await SqlServerService.GetColumnDetailsAsync(_connection.BuildConnectionString(), _database ?? "", _schema, _table);
            foreach (var c in cols)
            {
                var size = SizeOf(c);
                var dc = new DesignColumn
                {
                    OriginalName = c.Name,
                    OriginalType = c.TypeName,
                    OriginalSize = size,
                    OriginalNullable = c.Nullable,
                    Name = c.Name,
                    Type = c.TypeName,
                    Size = size,
                    Nullable = c.Nullable,
                    Identity = c.Identity,
                    DefaultValue = c.Default,
                    PrimaryKey = c.IsPrimaryKey,
                    OriginalPrimaryKey = c.IsPrimaryKey,
                    OriginalDefault = c.Default,
                    OriginalDefaultName = c.DefaultName
                };
                dc.PropertyChanged += OnColumnChanged;
                Columns.Add(dc);
            }
            _originalColumns = cols.Select(c => c.Name).ToList();

            var (pkName, pkCols) = await SqlServerService.GetPrimaryKeyAsync(_connection.BuildConnectionString(), _database ?? "", _schema, _table);
            _pkName = pkName;
            _originalPk = pkCols;

            foreach (var ix in await SqlServerService.GetIndexesAsync(_connection.BuildConnectionString(), _database ?? "", _schema, _table))
            {
                var di = new DesignIndex
                {
                    OriginalName = ix.Name,
                    Name = ix.Name,
                    Columns = string.Join(", ", ix.Columns),
                    Unique = ix.Unique,
                    OriginalSpec = IndexSpec(ix.Unique, ix.Columns)
                };
                di.PropertyChanged += OnColumnChanged;
                Indexes.Add(di);
            }
            _originalIndexNames = Indexes.Where(i => i.OriginalName is not null).Select(i => i.OriginalName!).ToList();

            Generate();
        }
        catch (Exception ex)
        {
            Messages = "Could not load columns: " + ex.Message;
        }
    }

    private static string? SizeOf(SqlServerService.ColumnDetail c)
    {
        var t = c.TypeName.ToLowerInvariant();
        return t switch
        {
            "char" or "varchar" or "binary" or "varbinary" => c.MaxLength == -1 ? "max" : c.MaxLength.ToString(),
            "nchar" or "nvarchar" => c.MaxLength == -1 ? "max" : (c.MaxLength / 2).ToString(),
            "decimal" or "numeric" => $"{c.Precision},{c.Scale}",
            _ => null
        };
    }

    private async Task LoadSqliteAsync()
    {
        try
        {
            var cs = _connection.BuildConnectionString();
            var cols = await SqliteService.GetColumnDetailsAsync(cs, _table);
            foreach (var c in cols)
            {
                var dc = new DesignColumn
                {
                    OriginalName = c.Name,
                    OriginalType = c.Type,
                    OriginalNullable = !c.NotNull,
                    Name = c.Name,
                    Type = string.IsNullOrWhiteSpace(c.Type) ? "TEXT" : c.Type,
                    Nullable = !c.NotNull,
                    DefaultValue = c.Default,
                    PrimaryKey = c.Pk > 0,
                    OriginalPrimaryKey = c.Pk > 0,
                    OriginalDefault = c.Default
                };
                dc.PropertyChanged += OnColumnChanged;
                Columns.Add(dc);
            }
            _originalColumns = cols.Select(c => c.Name).ToList();
            _originalPk = cols.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList();

            foreach (var ix in await SqliteService.GetIndexesAsync(cs, _table))
            {
                var di = new DesignIndex
                {
                    OriginalName = ix.Name,
                    Name = ix.Name,
                    Columns = string.Join(", ", ix.Columns),
                    Unique = ix.Unique,
                    OriginalSpec = IndexSpec(ix.Unique, ix.Columns)
                };
                di.PropertyChanged += OnColumnChanged;
                Indexes.Add(di);
            }
            _originalIndexNames = Indexes.Where(i => i.OriginalName is not null).Select(i => i.OriginalName!).ToList();

            Generate();
        }
        catch (Exception ex)
        {
            Messages = "Could not load columns: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddColumn()
    {
        var dc = new DesignColumn { Name = "Column" + (Columns.Count + 1), Type = IsSqlite ? "TEXT" : "int" };
        dc.PropertyChanged += OnColumnChanged;
        Columns.Add(dc);
    }

    [RelayCommand]
    private void RemoveColumn(DesignColumn? c)
    {
        if (c is null) return;
        c.PropertyChanged -= OnColumnChanged;
        Columns.Remove(c);
    }

    [RelayCommand]
    private void AddIndex()
    {
        var di = new DesignIndex { Name = $"IX_{TableName}_{Indexes.Count + 1}" };
        di.PropertyChanged += OnColumnChanged;
        Indexes.Add(di);
    }

    [RelayCommand]
    private void RemoveIndex(DesignIndex? i)
    {
        if (i is null) return;
        i.PropertyChanged -= OnColumnChanged;
        Indexes.Remove(i);
    }

    [RelayCommand]
    private void CopyScript()
    {
        if (string.IsNullOrWhiteSpace(GeneratedSql)) return;
        try { System.Windows.Clipboard.SetText(GeneratedSql); Messages = "Script copied to clipboard."; }
        catch { Messages = "Could not access the clipboard."; }
    }

    private static string IndexSpec(bool unique, IEnumerable<string> cols) =>
        $"{unique}|{string.Join(",", cols.Select(c => c.Trim()))}";

    private static string BracketCols(string csv) =>
        string.Join(", ", csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => $"[{s}]"));

    [RelayCommand]
    private async Task Save()
    {
        if (string.IsNullOrWhiteSpace(GeneratedSql)) return;
        Messages = "Saving…";
        try
        {
            if (IsSqlite)
                await SqliteService.ExecuteScriptAsync(_connection.BuildConnectionString(), GeneratedSql);
            else
                await SqlServerService.ExecuteAsync(_connection.BuildConnectionString(), _database ?? "", GeneratedSql);
            Messages = _isNew
                ? "Created successfully. Close and re-open Design to make further changes."
                : "Saved successfully.";
        }
        catch (Exception ex)
        {
            Messages = "Error: " + ex.Message;
        }
    }

    // ---- SQL generation --------------------------------------------------

    private string Fq => $"[{_schema}].[{TableName}]";

    private static string FullType(DesignColumn c)
    {
        var t = c.Type.ToLowerInvariant();
        if (t is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary")
            return $"{c.Type}({(string.IsNullOrWhiteSpace(c.Size) ? "50" : c.Size)})";
        if (t is "decimal" or "numeric")
            return $"{c.Type}({(string.IsNullOrWhiteSpace(c.Size) ? "18,0" : c.Size)})";
        return c.Type;
    }

    private void Generate()
    {
        var valid = Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (valid.Count == 0) { GeneratedSql = ""; return; }

        if (IsSqlite)
            GeneratedSql = _isNew ? GenerateSqliteCreate(valid) : GenerateSqliteRebuild(valid);
        else
            GeneratedSql = _isNew ? GenerateCreate(valid) : GenerateAlter(valid);
    }

    // ---- SQLite generation ----------------------------------------------

    private static bool IsIntegerType(string type) => type.ToUpperInvariant().Contains("INT");

    private static string SqliteType(DesignColumn c) =>
        string.IsNullOrWhiteSpace(c.Type) ? "TEXT" : c.Type.Trim();

    /// <summary>The CREATE TABLE statement (columns + PK), no indexes, no trailing semicolon.</summary>
    private string SqliteCreateTable(List<DesignColumn> cols, string name)
    {
        var pkCols = cols.Where(c => c.PrimaryKey).ToList();
        var inlinePk = pkCols.Count == 1;

        var lines = new List<string>();
        foreach (var c in cols)
        {
            var line = new StringBuilder($"  [{c.Name}] {SqliteType(c)}");
            if (inlinePk && c.PrimaryKey)
            {
                line.Append(" PRIMARY KEY");
                if (c.Identity && IsIntegerType(SqliteType(c))) line.Append(" AUTOINCREMENT");
            }
            if (!c.Nullable && !(inlinePk && c.PrimaryKey)) line.Append(" NOT NULL");
            if (!string.IsNullOrWhiteSpace(c.DefaultValue)) line.Append($" DEFAULT {c.DefaultValue}");
            lines.Add(line.ToString());
        }
        if (pkCols.Count > 1)
            lines.Add($"  PRIMARY KEY ({string.Join(", ", pkCols.Select(c => $"[{c.Name}]"))})");

        return $"CREATE TABLE [{name}] (\n{string.Join(",\n", lines)}\n)";
    }

    private string GenerateSqliteCreate(List<DesignColumn> cols)
    {
        var sb = new StringBuilder(SqliteCreateTable(cols, TableName));
        foreach (var ix in Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Columns)))
            sb.Append($";\nCREATE {(ix.Unique ? "UNIQUE " : "")}INDEX [{ix.Name}] ON [{TableName}] ({BracketCols(ix.Columns)})");
        return sb.ToString() + ";";
    }

    /// <summary>
    /// SQLite can't ALTER most things, so rebuild: create a new table with the desired schema,
    /// copy the kept columns across, drop the old table, rename, then recreate indexes.
    /// </summary>
    private string GenerateSqliteRebuild(List<DesignColumn> cols)
    {
        var kept = cols.Where(c => c.OriginalName is not null).ToList();
        var tmp = _table + "__nmc_new";

        var stmts = new List<string>
        {
            "PRAGMA foreign_keys=OFF",
            "BEGIN TRANSACTION",
            SqliteCreateTable(cols, tmp)
        };

        if (kept.Count > 0)
        {
            var target = string.Join(", ", kept.Select(c => $"[{c.Name}]"));
            var source = string.Join(", ", kept.Select(c => $"[{c.OriginalName}]"));
            stmts.Add($"INSERT INTO [{tmp}] ({target}) SELECT {source} FROM [{_table}]");
        }

        stmts.Add($"DROP TABLE [{_table}]");
        stmts.Add($"ALTER TABLE [{tmp}] RENAME TO [{TableName}]");

        foreach (var ix in Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Columns)))
            stmts.Add($"CREATE {(ix.Unique ? "UNIQUE " : "")}INDEX [{ix.Name}] ON [{TableName}] ({BracketCols(ix.Columns)})");

        stmts.Add("COMMIT");
        stmts.Add("PRAGMA foreign_keys=ON");
        return string.Join(";\n", stmts) + ";";
    }

    private string GenerateCreate(List<DesignColumn> cols)
    {
        var sb = new StringBuilder($"CREATE TABLE [{_schema}].[{TableName}] (\n");
        var lines = new List<string>();
        foreach (var c in cols)
        {
            var line = new StringBuilder($"  [{c.Name}] {FullType(c)}");
            if (c.Identity) line.Append(" IDENTITY(1,1)");
            line.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (!string.IsNullOrWhiteSpace(c.DefaultValue)) line.Append($" DEFAULT {c.DefaultValue}");
            lines.Add(line.ToString());
        }
        var pk = cols.Where(c => c.PrimaryKey).Select(c => $"[{c.Name}]").ToList();
        if (pk.Count > 0)
            lines.Add($"  CONSTRAINT [PK_{TableName}] PRIMARY KEY ({string.Join(", ", pk)})");

        sb.Append(string.Join(",\n", lines)).Append("\n)");

        foreach (var ix in Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Columns)))
            sb.Append($";\nCREATE {(ix.Unique ? "UNIQUE " : "")}INDEX [{ix.Name}] ON [{_schema}].[{TableName}] ({BracketCols(ix.Columns)})");

        return sb.ToString();
    }

    private string GenerateAlter(List<DesignColumn> cols)
    {
        var statements = new List<string>();
        var currentOriginals = cols.Where(c => c.OriginalName is not null)
            .Select(c => c.OriginalName!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Drop changed/removed indexes first (they may reference columns/keys being changed).
        var keepIndexNames = Indexes.Where(i => i.OriginalName is not null).Select(i => i.OriginalName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Indexes.Where(i => i.OriginalName is not null))
        {
            var spec = IndexSpec(i.Unique, i.Columns.Split(','));
            if (!string.Equals(spec, i.OriginalSpec, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(i.Name, i.OriginalName, StringComparison.Ordinal))
                changedExisting.Add(i.OriginalName!);
        }
        // dropped indexes (no longer present) + changed indexes
        foreach (var orig in _originalIndexNames)
            if (!keepIndexNames.Contains(orig) || changedExisting.Contains(orig))
                statements.Add($"DROP INDEX [{orig}] ON {Fq}");

        // 2. PK change: drop old PK if the set differs.
        var currentPk = cols.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        var pkChanged = !currentPk.SequenceEqual(_originalPk, StringComparer.OrdinalIgnoreCase);
        if (pkChanged && _pkName is not null)
            statements.Add($"ALTER TABLE {Fq} DROP CONSTRAINT [{_pkName}]");

        // 3. Drop removed columns.
        foreach (var orig in _originalColumns)
            if (!currentOriginals.Contains(orig))
                statements.Add($"ALTER TABLE {Fq} DROP COLUMN [{orig}]");

        // 4. Rename + alter existing columns.
        foreach (var c in cols.Where(c => c.OriginalName is not null))
        {
            if (!string.Equals(c.Name, c.OriginalName, StringComparison.Ordinal))
                statements.Add($"EXEC sp_rename '{_schema}.{TableName}.{c.OriginalName}', '{c.Name}', 'COLUMN'");

            var newType = FullType(c);
            var oldType = FullType(new DesignColumn { Type = c.OriginalType ?? "int", Size = c.OriginalSize });
            if (!string.Equals(newType, oldType, StringComparison.OrdinalIgnoreCase) || c.Nullable != c.OriginalNullable)
                statements.Add($"ALTER TABLE {Fq} ALTER COLUMN [{c.Name}] {newType} {(c.Nullable ? "NULL" : "NOT NULL")}");
        }

        // 5. Add new columns.
        foreach (var c in cols.Where(c => c.OriginalName is null && !string.IsNullOrWhiteSpace(c.Name)))
        {
            var line = new StringBuilder($"ALTER TABLE {Fq} ADD [{c.Name}] {FullType(c)}");
            if (c.Identity) line.Append(" IDENTITY(1,1)");
            line.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (!string.IsNullOrWhiteSpace(c.DefaultValue)) line.Append($" DEFAULT {c.DefaultValue}");
            statements.Add(line.ToString());
        }

        // 6. Default-constraint changes on existing columns.
        foreach (var c in cols.Where(c => c.OriginalName is not null))
        {
            var cur = (c.DefaultValue ?? "").Trim();
            var old = (c.OriginalDefault ?? "").Trim();
            if (string.Equals(cur, old, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(c.OriginalDefaultName))
                statements.Add($"ALTER TABLE {Fq} DROP CONSTRAINT [{c.OriginalDefaultName}]");
            if (cur.Length > 0)
                statements.Add($"ALTER TABLE {Fq} ADD CONSTRAINT [DF_{TableName}_{c.Name}] DEFAULT {cur} FOR [{c.Name}]");
        }

        // 7. Add the new PK.
        if (pkChanged && currentPk.Count > 0)
            statements.Add($"ALTER TABLE {Fq} ADD CONSTRAINT [PK_{TableName}] PRIMARY KEY ({string.Join(", ", currentPk.Select(n => $"[{n}]"))})");

        // 8. Create new / changed indexes.
        foreach (var i in Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Columns)))
        {
            var isNew = i.OriginalName is null;
            var isChanged = i.OriginalName is not null && changedExisting.Contains(i.OriginalName);
            if (isNew || isChanged)
                statements.Add($"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX [{i.Name}] ON {Fq} ({BracketCols(i.Columns)})");
        }

        if (statements.Count == 0) return "-- No changes.";
        return string.Join(";\n", statements) + ";";
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Generate();
    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e) => Generate();
}
