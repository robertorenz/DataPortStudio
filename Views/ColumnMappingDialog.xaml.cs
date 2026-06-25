using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>
/// Lets the user review (and tweak) the SQL type chosen for each column when copying a Clarion
/// file (.tps / .dat) into a SQL database, before the table is created and the rows are copied.
/// The SQL type is chosen from an engine-specific dropdown plus a size/precision field.
/// </summary>
public partial class ColumnMappingDialog : Window
{
    private readonly List<TableCopyService.ClarionColumnMap> _suggested;
    private readonly IReadOnlyList<string> _baseTypes;
    private readonly HashSet<string> _sized;

    public ObservableCollection<ColumnMapRow> Rows { get; } = new();

    /// <summary>The confirmed mapping (null until the user clicks Apply).</summary>
    public List<TableCopyService.ClarionColumnMap>? Result { get; private set; }

    public ColumnMappingDialog(string sourceName, string targetDescription, DatabaseEngine targetEngine,
        IEnumerable<TableCopyService.ClarionColumnMap> proposed)
    {
        InitializeComponent();
        (_baseTypes, _sized) = TypeCatalog(targetEngine);
        _suggested = proposed.ToList();
        SubtitleText.Text = $"'{sourceName}'  →  {targetDescription}";

        foreach (var m in _suggested)
        {
            var (baseType, size) = Split(m.TargetType);
            Rows.Add(new ColumnMapRow(IsSized)
            {
                Name = m.Name,
                SourceType = m.SourceType,
                BaseTypes = _baseTypes,
                BaseType = baseType,
                Size = size
            });
        }
        RowList.ItemsSource = Rows;
    }

    private bool IsSized(string? baseType) => _sized.Contains((baseType ?? "").Trim().ToLowerInvariant());

    private static bool IsTextOrBinary(string? baseType) =>
        (baseType ?? "").Trim().ToLowerInvariant() is
            "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary";

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < Rows.Count && i < _suggested.Count; i++)
        {
            var (baseType, size) = Split(_suggested[i].TargetType);
            Rows[i].BaseType = baseType;
            Rows[i].Size = size;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var blank = Rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.BaseType));
        if (blank is not null)
        {
            Dialogs.ShowError("Missing type", $"Please choose a SQL type for column '{blank.Name}'.");
            return;
        }

        // Text/binary types need an explicit length (otherwise most engines default to 1).
        var needsLength = Rows.FirstOrDefault(r =>
            IsTextOrBinary(r.BaseType) && string.IsNullOrWhiteSpace(r.Size));
        if (needsLength is not null)
        {
            Dialogs.ShowError("Missing size",
                $"Set a length for column '{needsLength.Name}' ({needsLength.BaseType.Trim()}) — e.g. 80 or max.");
            return;
        }
        Result = Rows
            .Select(r => new TableCopyService.ClarionColumnMap(r.Name, r.SourceType, r.Compose()))
            .ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Splits "nvarchar(80)" / "decimal(11,0)" / "int" into (baseType, size).</summary>
    private static (string BaseType, string Size) Split(string type)
    {
        var t = (type ?? "").Trim();
        var open = t.IndexOf('(');
        if (open < 0) return (t, "");
        var close = t.LastIndexOf(')');
        var baseType = t[..open].Trim();
        var size = close > open ? t[(open + 1)..close].Trim() : t[(open + 1)..].Trim();
        return (baseType, size);
    }

    /// <summary>Base SQL types offered for an engine, and which of them take a size/precision argument.</summary>
    private static (IReadOnlyList<string> Types, HashSet<string> Sized) TypeCatalog(DatabaseEngine e)
    {
        switch (e)
        {
            case DatabaseEngine.MySql:
            case DatabaseEngine.MariaDb:
                return (new[]
                {
                    "tinyint", "smallint", "mediumint", "int", "bigint", "decimal", "float", "double", "bit",
                    "date", "time", "datetime", "timestamp", "year",
                    "char", "varchar", "text", "mediumtext", "longtext", "tinytext",
                    "binary", "varbinary", "blob", "longblob"
                }, new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "binary", "varbinary", "decimal" });

            case DatabaseEngine.Firebird:
                return (new[]
                {
                    "smallint", "integer", "bigint", "decimal", "numeric", "float", "double precision",
                    "date", "time", "timestamp",
                    "char", "varchar", "blob sub_type text", "blob"
                }, new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "decimal", "numeric" });

            case DatabaseEngine.Sqlite:
                return (new[] { "INTEGER", "REAL", "TEXT", "NUMERIC", "BLOB" },
                    new(StringComparer.OrdinalIgnoreCase)); // SQLite ignores sizes

            default: // SQL Server
                return (new[]
                {
                    "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "float", "real",
                    "date", "time", "datetime2", "datetime", "smalldatetime", "datetimeoffset",
                    "char", "varchar", "nchar", "nvarchar", "text", "ntext",
                    "binary", "varbinary", "uniqueidentifier"
                }, new(StringComparer.OrdinalIgnoreCase)
                    { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary", "decimal", "numeric" });
        }
    }
}

/// <summary>A single editable row in the column-mapping dialog (base SQL type + size).</summary>
public sealed class ColumnMapRow : INotifyPropertyChanged
{
    private readonly Func<string?, bool> _isSized;

    public ColumnMapRow(Func<string?, bool> isSized) => _isSized = isSized;

    public string Name { get; init; } = "";
    public string SourceType { get; init; } = "";
    public IReadOnlyList<string> BaseTypes { get; init; } = Array.Empty<string>();

    private string _baseType = "";
    public string BaseType
    {
        get => _baseType;
        set
        {
            if (_baseType == value) return;
            _baseType = value;
            OnChanged(nameof(BaseType));
            OnChanged(nameof(SizeEnabled));
        }
    }

    private string _size = "";
    public string Size
    {
        get => _size;
        set { if (_size != value) { _size = value; OnChanged(nameof(Size)); } }
    }

    /// <summary>True when the chosen base type accepts a size/precision (text/binary/decimal).</summary>
    public bool SizeEnabled => _isSized(_baseType);

    /// <summary>The full SQL type, e.g. "nvarchar(80)" or "int".</summary>
    public string Compose() =>
        SizeEnabled && !string.IsNullOrWhiteSpace(_size)
            ? $"{_baseType.Trim()}({_size.Trim()})"
            : _baseType.Trim();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
