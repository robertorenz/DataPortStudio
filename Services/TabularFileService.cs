using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DataPortStudio.Services;

/// <summary>The file formats a folder connection can open as tables.</summary>
public enum TabularFormat { Xlsx, Xls, Csv, Tsv, Json, Xml }

/// <summary>One openable table in a folder: an Excel worksheet, or a whole single-table text file.</summary>
public record TabularSheet(string DisplayName, string FileName, string SheetName);

/// <summary>
/// A folder connection: every Excel worksheet and every CSV / TSV / JSON / XML file in the folder
/// appears as a table. File name is stored in Node.Database, sheet name in Node.Schema — for the
/// single-table text formats the "sheet" is the file's own base name.
/// Excel work is delegated to <see cref="ExcelService"/>; the text formats are read here.
/// </summary>
public static class TabularFileService
{
    // "*.xls" also matches "*.xlsx" through Win32 FindFirstFile, so every result is re-checked
    // against FormatOf and deduplicated before use.
    private static readonly string[] SearchPatterns =
        ["*.xlsx", "*.xlsm", "*.xls", "*.csv", "*.tsv", "*.tab", "*.json", "*.xml"];

    public static TabularFormat? FormatOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" => TabularFormat.Xlsx,
            ".xls" => TabularFormat.Xls,
            ".csv" => TabularFormat.Csv,
            ".tsv" or ".tab" => TabularFormat.Tsv,
            ".json" => TabularFormat.Json,
            ".xml" => TabularFormat.Xml,
            _ => null
        };

    public static bool IsSupportedFile(string path) => FormatOf(path) is not null;

    public static string FormatName(TabularFormat format) => format switch
    {
        TabularFormat.Xlsx or TabularFormat.Xls => "Excel",
        TabularFormat.Csv => "CSV",
        TabularFormat.Tsv => "TSV",
        TabularFormat.Json => "JSON",
        TabularFormat.Xml => "XML",
        _ => format.ToString()
    };

    /// <summary>Excel workbooks hold many sheets; every text format is a single table.</summary>
    public static bool HasWorksheets(TabularFormat format) =>
        format is TabularFormat.Xlsx or TabularFormat.Xls;

    /// <summary>
    /// JSON and XML open read-only: their values can nest, and the grid flattens nested values to
    /// text, so writing the file back would silently destroy the structure.
    /// </summary>
    public static bool IsEditable(TabularFormat format) =>
        format is not (TabularFormat.Json or TabularFormat.Xml);

    /// <summary>Whether the given file can be edited and saved. Unknown extensions are not editable.</summary>
    public static bool IsEditableFile(string fileName) =>
        FormatOf(fileName) is { } format && IsEditable(format);

    /// <summary>Lists supported file names in the folder (sorted). Does not open any files.</summary>
    public static List<string> ListFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];
        return SearchPatterns
            .SelectMany(p => Directory.EnumerateFiles(folder, p, SearchOption.TopDirectoryOnly))
            .Where(IsSupportedFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the table names inside one file: worksheet names for Excel (in workbook order), or
    /// the file's own base name for the single-table text formats.
    /// </summary>
    public static List<string> ListSheetsForFile(string folder, string fileName)
    {
        var format = FormatOf(fileName);
        if (format is null) return [];
        if (HasWorksheets(format.Value)) return ExcelService.ListSheetsForFile(folder, fileName);
        return [Path.GetFileNameWithoutExtension(fileName)];
    }

    /// <summary>Lists every table across every supported file in the folder.</summary>
    public static List<TabularSheet> ListSheets(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var result = new List<TabularSheet>();
        foreach (var fileName in ListFiles(folder))
        {
            var format = FormatOf(fileName);
            if (format is null) continue;
            if (HasWorksheets(format.Value))
            {
                // Skip corrupt or locked workbooks rather than failing the whole folder.
                foreach (var sheet in ExcelService.ListSheetsForFile(folder!, fileName))
                    result.Add(new TabularSheet($"{fileName} — {sheet}", fileName, sheet));
            }
            else
            {
                result.Add(new TabularSheet(
                    fileName, fileName, Path.GetFileNameWithoutExtension(fileName)));
            }
        }
        return result;
    }

    /// <summary>
    /// Confirms the folder exists and holds at least one readable table. Stops at the first one
    /// found rather than scanning the whole folder, so testing a folder of large workbooks is quick.
    /// </summary>
    public static void TestConnection(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException(
                "Choose a folder that contains Excel, CSV, TSV, JSON or XML files.");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        foreach (var fileName in ListFiles(folder))
        {
            var format = FormatOf(fileName);
            if (format is null) continue;
            // A text file is a table in its own right; a workbook only counts if it has a sheet.
            if (!HasWorksheets(format.Value)) return;
            if (ExcelService.ListSheetsForFile(folder!, fileName).Count > 0) return;
        }

        throw new FileNotFoundException(
            $"No Excel (.xls / .xlsx), CSV, TSV, JSON or XML files were found in {folder}.");
    }

    /// <summary>Reads one table into a DataTable. First row / object keys become columns; values are strings.</summary>
    public static DataTable ReadTable(string folder, string fileName, string sheetName, int rowLimit)
    {
        var path = Path.Combine(folder, fileName);
        var format = FormatOf(path)
            ?? throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(path)}");

        return format switch
        {
            TabularFormat.Xlsx or TabularFormat.Xls =>
                ExcelService.ReadTable(folder, fileName, sheetName, rowLimit),
            TabularFormat.Csv => ReadDelimited(path, DetectDelimiter(path), rowLimit),
            TabularFormat.Tsv => ReadDelimited(path, '\t', rowLimit),
            TabularFormat.Json => ReadJson(path, rowLimit),
            TabularFormat.Xml => ReadXml(path, rowLimit),
            _ => throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(path)}")
        };
    }

    /// <summary>Writes the grid back to the file. Only Excel, CSV and TSV are writable.</summary>
    public static void SaveTable(string folder, string fileName, string sheetName, DataTable table)
    {
        var path = Path.Combine(folder, fileName);
        var format = FormatOf(path)
            ?? throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(path)}");

        switch (format)
        {
            case TabularFormat.Xlsx:
            case TabularFormat.Xls:
                ExcelService.SaveTable(folder, fileName, sheetName, table);
                break;
            case TabularFormat.Csv:
                WriteDelimited(path, table, DetectDelimiter(path));
                break;
            case TabularFormat.Tsv:
                WriteDelimited(path, table, '\t');
                break;
            default:
                throw new NotSupportedException(
                    $"{FormatName(format)} files are opened read-only, so there is nothing to save.");
        }
    }

    // ---- delimited text (CSV / TSV) -------------------------------------

    /// <summary>
    /// Opens the file for reading while tolerating another process holding it open, and honours a
    /// UTF-8 / UTF-16 byte-order mark when there is one.
    /// </summary>
    private static StreamReader OpenText(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    /// <summary>
    /// Sniffs the separator from the first non-empty line, so semicolon-separated exports from
    /// European locales open correctly too. Falls back to a comma.
    /// </summary>
    private static char DetectDelimiter(string path)
    {
        try
        {
            using var reader = OpenText(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var best = ',';
                var bestCount = 0;
                foreach (var candidate in new[] { ',', ';', '\t', '|' })
                {
                    var count = CountOutsideQuotes(line, candidate);
                    if (count > bestCount) { bestCount = count; best = candidate; }
                }
                return bestCount > 0 ? best : ',';
            }
        }
        catch { /* unreadable — fall back below */ }
        return ',';
    }

    private static int CountOutsideQuotes(string line, char target)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (!inQuotes && ch == target) count++;
        }
        return count;
    }

    /// <summary>RFC 4180 style split: quoted fields may contain the delimiter, newlines and "" escapes.</summary>
    private static List<string[]> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { /* handled by the \n case */ }
            else if (ch == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    private static DataTable ReadDelimited(string path, char delimiter, int rowLimit)
    {
        string text;
        using (var reader = OpenText(path)) text = reader.ReadToEnd();

        var table = new DataTable(Path.GetFileNameWithoutExtension(path));
        var rows = ParseDelimited(text, delimiter);
        if (rows.Count == 0) return table;

        var header = rows[0];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < header.Length; c++)
            table.Columns.Add(UniqueName(header[c], c, used), typeof(string));
        if (table.Columns.Count == 0) return table;

        var count = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            if (rowLimit > 0 && count >= rowLimit) break;
            var values = rows[r];
            if (values.All(string.IsNullOrEmpty)) continue;

            var dr = table.NewRow();
            for (var c = 0; c < table.Columns.Count; c++)
                dr[c] = c < values.Length ? Value(values[c]) : DBNull.Value;
            table.Rows.Add(dr);
            count++;
        }

        table.AcceptChanges();
        return table;
    }

    private static void WriteDelimited(string path, DataTable table, char delimiter)
    {
        var columns = table.Columns.Cast<DataColumn>().ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, columns.Select(c => Escape(c.ColumnName, delimiter))));

        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            sb.AppendLine(string.Join(delimiter, columns.Select(c =>
            {
                var value = row[c];
                return Escape(value is null or DBNull ? "" : value.ToString() ?? "", delimiter);
            })));
        }

        // Write the BOM back so Excel keeps reading accented text correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value, char delimiter)
    {
        var needsQuotes = value.Contains(delimiter) || value.Contains('"')
                          || value.Contains('\n') || value.Contains('\r');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ---- JSON -----------------------------------------------------------

    private static DataTable ReadJson(string path, int rowLimit)
    {
        string text;
        using (var reader = OpenText(path)) text = reader.ReadToEnd();

        var table = new DataTable(Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(text)) return table;

        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var rows = new List<JsonElement>();
        foreach (var element in SelectJsonRows(document.RootElement))
        {
            if (rowLimit > 0 && rows.Count >= rowLimit) break;
            rows.Add(element);
        }
        if (rows.Count == 0) return table;

        // Columns are the union of every object's keys, in the order they are first seen.
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasScalarRows = false;
        foreach (var element in rows)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    if (seen.Add(property.Name)) columns.Add(property.Name);
            }
            else hasScalarRows = true;
        }
        if (hasScalarRows && seen.Add(ScalarColumn)) columns.Add(ScalarColumn);
        if (columns.Count == 0) return table;

        foreach (var column in columns) table.Columns.Add(column, typeof(string));

        foreach (var element in rows)
        {
            var dr = table.NewRow();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    if (table.Columns.Contains(property.Name))
                        dr[property.Name] = JsonValue(property.Value);
            }
            else dr[ScalarColumn] = JsonValue(element);
            table.Rows.Add(dr);
        }

        table.AcceptChanges();
        return table;
    }

    /// <summary>
    /// Finds the array of records: a bare array, the legacy <c>{"RECORDS":[…]}</c> wrapper this app
    /// can export, any other single array property, or a lone object treated as one row.
    /// </summary>
    private static IEnumerable<JsonElement> SelectJsonRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("RECORDS", out var records) &&
                records.ValueKind == JsonValueKind.Array)
                return records.EnumerateArray();

            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Array)
                    return property.Value.EnumerateArray();

            return [root];
        }

        return [root];
    }

    private static object JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.String => Value(value.GetString()),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.Number => value.GetRawText(),
        // Nested objects and arrays are shown as compact JSON, the same way MongoDB documents are.
        _ => JsonSerializer.Serialize(value)
    };

    // ---- XML ------------------------------------------------------------

    private static DataTable ReadXml(string path, int rowLimit)
    {
        var table = new DataTable(Path.GetFileNameWithoutExtension(path));

        XDocument document;
        using (var reader = OpenText(path)) document = XDocument.Load(reader);
        if (document.Root is null) return table;

        var rows = SelectXmlRows(document.Root);
        if (rowLimit > 0) rows = rows.Take(rowLimit).ToList();
        if (rows.Count == 0) return table;

        // Columns are the union of every row's attributes and child elements, first seen first.
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasTextOnlyRows = false;
        foreach (var row in rows)
        {
            foreach (var attribute in row.Attributes())
                if (!attribute.IsNamespaceDeclaration && seen.Add(attribute.Name.LocalName))
                    columns.Add(attribute.Name.LocalName);
            foreach (var child in row.Elements())
                if (seen.Add(child.Name.LocalName)) columns.Add(child.Name.LocalName);
            if (!row.HasAttributes && !row.HasElements) hasTextOnlyRows = true;
        }
        if (hasTextOnlyRows && seen.Add(ScalarColumn)) columns.Add(ScalarColumn);
        if (columns.Count == 0) return table;

        foreach (var column in columns) table.Columns.Add(column, typeof(string));

        foreach (var row in rows)
        {
            var dr = table.NewRow();
            foreach (var attribute in row.Attributes())
                if (!attribute.IsNamespaceDeclaration && table.Columns.Contains(attribute.Name.LocalName))
                    dr[attribute.Name.LocalName] = Value(attribute.Value);
            foreach (var child in row.Elements())
                if (table.Columns.Contains(child.Name.LocalName))
                    // Nested elements keep their markup, so nothing is silently dropped.
                    dr[child.Name.LocalName] = child.HasElements
                        ? Value(string.Concat(child.Nodes()
                            .Select(n => n.ToString(SaveOptions.DisableFormatting))))
                        : Value(child.Value);
            if (!row.HasAttributes && !row.HasElements && table.Columns.Contains(ScalarColumn))
                dr[ScalarColumn] = Value(row.Value);
            table.Rows.Add(dr);
        }

        table.AcceptChanges();
        return table;
    }

    /// <summary>
    /// Picks the repeating element that represents a row — the largest same-named group under the
    /// root, looking one level deeper when the root only wraps a single container
    /// (e.g. <c>&lt;dataset&gt;&lt;rows&gt;&lt;row/&gt;…</c>).
    /// </summary>
    private static List<XElement> SelectXmlRows(XElement root)
    {
        var groups = root.Elements().GroupBy(e => e.Name.LocalName).ToList();
        if (groups.Count == 0) return [];

        var largest = groups.OrderByDescending(g => g.Count()).First();
        if (largest.Count() > 1) return largest.ToList();

        var wrapper = root.Elements().First();
        var innerLargest = wrapper.Elements()
            .GroupBy(e => e.Name.LocalName)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (innerLargest is not null && innerLargest.Count() > 1) return innerLargest.ToList();

        return largest.ToList();
    }

    // ---- structure panel ------------------------------------------------

    /// <summary>Structure / info panel for a worksheet or a single-table text file.</summary>
    public static Task<TableStructure> GetStructureAsync(
        string folder, string fileName, string sheetName, string connectionName = "")
    {
        var format = FormatOf(fileName);
        if (format is { } excel && HasWorksheets(excel))
            return ExcelService.GetStructureAsync(folder, fileName, sheetName, connectionName);

        var fi = new FileInfo(Path.Combine(folder, fileName));
        var name = format is null ? "File" : FormatName(format.Value);

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{"Connection",w}{connectionName}");
        info.AppendLine($"{"File",w}{fi.Name}");
        info.AppendLine($"{"Folder",w}{folder}");
        info.AppendLine($"{"Format",w}{name}");
        if (fi.Exists)
        {
            info.AppendLine($"{"Size",w}{FormatSize(fi.Length)}");
            info.AppendLine($"{"Modified",w}{fi.LastWriteTime:yyyy-MM-dd HH:mm}");
        }

        var editable = format is { } f && IsEditable(f);
        var ddl = $"-- {name} file — no SQL DDL.\n" + (editable
            ? "-- Edit cells, add and delete rows, then Save to write the file back."
            : "-- Opened read-only: rewriting the file would flatten its nested values.");

        return Task.FromResult(new TableStructure(ddl, info.ToString().TrimEnd(),
            $"{name} files have no foreign-key relationships."));
    }

    // ---- shared helpers -------------------------------------------------

    /// <summary>Column name used when a record is a bare value rather than a set of fields.</summary>
    private const string ScalarColumn = "value";

    private static object Value(string? text) =>
        string.IsNullOrEmpty(text) ? DBNull.Value : text;

    private static string UniqueName(string? name, int index, HashSet<string> used)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? $"Column{index + 1}" : name.Trim();
        if (used.Add(candidate)) return candidate;
        for (var n = 2; ; n++)
            if (used.Add($"{candidate}_{n}")) return $"{candidate}_{n}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:N0} KB";
        var mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:N1} MB" : $"{mb / 1024.0:N2} GB";
    }
}
