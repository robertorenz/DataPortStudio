using System.Data;
using System.IO;
using System.Text;
using TpsParser;
using TpsParser.TypeModel;

namespace DataPortStudio.Services;

/// <summary>
/// Reads Clarion TopSpeed (.tps) files (read-only viewer + copy source).
///
/// A "TPS connection" is just a folder: each <c>*.tps</c> file in it is exposed as a table.
/// Records are decoded with the TpsParser library into a plain <see cref="DataTable"/> so the
/// rest of the app (grid, filter/sort, export, Clarion date/time detection, cross-engine copy)
/// works unchanged. There is no SQL and no writing back.
/// </summary>
public static class TpsService
{
    private const string Extension = ".tps";

    // Clarion text is typically Windows-1252; Latin1 decodes any byte without throwing.
    private static readonly Encoding TextEncoding = Encoding.Latin1;

    /// <summary>Lists the .tps files in a folder as table names (file name without extension), sorted.</summary>
    public static List<string> ListTables(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new List<string>();

        var names = Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Confirms the folder exists and contains at least one .tps file.</summary>
    public static void TestConnection(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Choose a folder that contains .tps files.");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        if (ListTables(folder).Count == 0)
            throw new FileNotFoundException($"No .tps files were found in {folder}.");
    }

    /// <summary>Resolves a table name back to its .tps file path (case-insensitive, tolerant of the extension).</summary>
    private static string ResolvePath(string folder, string tableName)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("No TPS folder is set for this connection.");

        var direct = Path.Combine(folder, tableName + Extension);
        if (File.Exists(direct)) return direct;

        // Fall back to a case-insensitive scan (file systems and the original casing may differ).
        var match = Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), tableName, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new FileNotFoundException($"'{tableName}{Extension}' was not found in {folder}.");
    }

    private static TpsFile Open(Stream stream) => new(stream);

    /// <summary>
    /// Reads a .tps file into a DataTable. <paramref name="rowLimit"/> caps the rows materialized
    /// (use 0 for structure-only, int.MaxValue for everything). Columns are always built from the
    /// table definition, so empty tables still produce a typed, copyable schema.
    /// </summary>
    public static DataTable ReadTable(string folder, string tableName, int rowLimit)
    {
        var path = ResolvePath(folder, tableName);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = Open(fs);

        var defs = file.GetTableDefinitions();
        if (defs.Count == 0)
            throw new InvalidOperationException($"'{tableName}' contains no table definition.");

        // One .tps file usually holds exactly one logical table — take the first.
        var tableNumber = defs.Keys.First();
        var def = defs[tableNumber];

        var table = new DataTable(tableName);
        var fields = BuildColumns(table, def);

        if (rowLimit > 0)
        {
            var rows = Table.MaterializeFromFile(file, tableNumber).Rows;
            var count = 0;
            foreach (var row in rows)
            {
                if (count++ >= rowLimit) break;
                var dr = table.NewRow();
                foreach (var (column, fieldName, isMemo) in fields)
                {
                    dr[column] = isMemo
                        ? MemoValue(row.GetMemoCaseInsensitive(fieldName, isRequired: false), table.Columns[column]!.DataType)
                        : FieldValue(row.GetFieldValueCaseInsensitive(fieldName, isRequired: false));
                }
                table.Rows.Add(dr);
            }
        }

        table.AcceptChanges();
        return table;
    }

    /// <summary>Adds columns for a table definition's fields then memos; returns the (column, field, isMemo) map.</summary>
    private static List<(string Column, string Field, bool IsMemo)> BuildColumns(DataTable table, TableDefinition def)
    {
        var map = new List<(string, string, bool)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Unique(string name)
        {
            var candidate = string.IsNullOrEmpty(name) ? "Field" : name;
            var n = candidate;
            var i = 2;
            while (!used.Add(n)) n = $"{candidate}_{i++}";
            return n;
        }

        foreach (var f in def.Fields)
        {
            var col = Unique(f.Name);
            var dc = table.Columns.Add(col, f.IsArray ? typeof(string) : ClrType(f.TypeCode));
            if (dc.DataType == typeof(string) && f.StringLength > 0) dc.MaxLength = f.StringLength;
            if (dc.DataType == typeof(decimal))
            {
                // Preserve BCD precision/scale so the SQL target column keeps its decimals.
                var scale = f.BcdDigitsAfterDecimalPoint;
                var prec = Math.Max(scale + 1, f.BcdElementLength * 2 - 1);
                dc.ExtendedProperties["prec"] = (int)prec;
                dc.ExtendedProperties["scale"] = (int)scale;
            }
            map.Add((col, f.Name, false));
        }

        foreach (var m in def.Memos)
        {
            var col = Unique(m.Name);
            table.Columns.Add(col, m.IsBlob ? typeof(byte[]) : typeof(string));
            map.Add((col, m.Name, true));
        }

        return map;
    }

    private static Type ClrType(FieldTypeCode code) => code switch
    {
        FieldTypeCode.Byte => typeof(byte),
        FieldTypeCode.Short => typeof(short),
        FieldTypeCode.UShort => typeof(int),
        FieldTypeCode.Long => typeof(int),
        FieldTypeCode.ULong => typeof(long),
        FieldTypeCode.Date => typeof(DateTime),
        FieldTypeCode.Time => typeof(TimeSpan),
        FieldTypeCode.SReal => typeof(float),
        FieldTypeCode.Real => typeof(double),
        FieldTypeCode.Decimal => typeof(decimal),
        _ => typeof(string) // FString/CString/PString/Group/None
    };

    private static object FieldValue(IClaObject? value) => value switch
    {
        null => DBNull.Value,
        ClaDate d => d.Value is { } day ? day.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
        ClaTime t => t.Value.ToTimeSpan(),
        ClaByte b => b.Value,
        ClaShort s => s.Value,
        ClaUnsignedShort us => (int)us.Value,
        ClaLong l => l.Value,
        ClaUnsignedLong ul => (long)ul.Value,
        ClaSingleReal sr => sr.Value,
        ClaReal r => r.Value,
        ClaDecimal dec => dec.ToDecimal() is { HasValue: true } m ? m.Value : DBNull.Value,
        IClaString str => Clean(str.StringValue),
        _ => value.ToString() ?? ""
    };

    private static object MemoValue(ITpsMemo? memo, Type columnType)
    {
        if (memo is null) return DBNull.Value;
        var bytes = memo.ToArray();
        if (columnType == typeof(byte[])) return bytes;
        return Clean(TextEncoding.GetString(bytes));
    }

    /// <summary>Clarion fixed-length strings are space/null padded — trim the trailing fill.</summary>
    private static object Clean(string? s)
    {
        if (s is null) return DBNull.Value;
        var trimmed = s.TrimEnd('\0', ' ');
        return trimmed;
    }

    /// <summary>Structure/info panel for a TPS table.</summary>
    public static Task<TableStructure> GetStructureAsync(string folder, string tableName, string connectionName = "")
    {
        var path = ResolvePath(folder, tableName);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = Open(fs);
        var defs = file.GetTableDefinitions();
        var def = defs.Count > 0 ? defs[defs.Keys.First()] : null;

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{"Connection",w}{connectionName}");
        info.AppendLine($"{"File",w}{Path.GetFileName(path)}");
        info.AppendLine($"{"Folder",w}{folder}");
        if (def is not null)
        {
            info.AppendLine($"{"Fields",w}{def.Fields.Length}");
            info.AppendLine($"{"Memos",w}{def.Memos.Length}");
            info.AppendLine($"{"Record length",w}{def.RecordLength} bytes");
            info.AppendLine();
            info.AppendLine("Fields:");
            foreach (var f in def.Fields)
                info.AppendLine($"  • {f.Name}  ({f.TypeCode}{(f.IsArray ? $"[{f.ElementCount}]" : "")})");
            foreach (var m in def.Memos)
                info.AppendLine($"  • {m.Name}  ({(m.IsBlob ? "BLOB" : "MEMO")})");
        }

        var ddl = "-- Clarion TPS is a fixed binary format — it has no SQL DDL.\n" +
                  $"-- '{tableName}' is read-only; use Copy to write it to a SQL database.";

        return Task.FromResult(new TableStructure(ddl, info.ToString().TrimEnd(),
            "TPS files have no foreign-key relationships."));
    }
}
