using System.Data;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace DataPortStudio.Services;

public enum ExportFormat { Dbf, Txt, Csv, Tsv, Html, Xls, Xlsx, Sql, Xml, Json }

public enum DateOrder { DMY, MDY, YMD }
public enum BinaryEncoding { Base64, Hex }

/// <summary>Format-specific options (currently used by JSON export).</summary>
public sealed class ExportOptions
{
    public bool JsonLegacyRecordsKey { get; set; }
    public bool ZeroPaddingDate { get; set; } = true;

    /// <summary>Emit a GO statement every N INSERT rows (0 = no GO). SQL Server batch separator.</summary>
    public int SqlBatchSize { get; set; } = 100;

    /// <summary>Wrap SQL export in SET NOCOUNT ON / OFF.</summary>
    public bool SqlNoCount { get; set; } = true;
    public DateOrder DateOrder { get; set; } = DateOrder.DMY;
    public string DateDelimiter { get; set; } = "/";
    public string TimeDelimiter { get; set; } = ":";
    public string DecimalSymbol { get; set; } = ".";
    public BinaryEncoding Binary { get; set; } = BinaryEncoding.Base64;

    public static readonly ExportOptions Default = new();

    public string FormatDate(DateTime dt)
    {
        string d = ZeroPaddingDate ? dt.Day.ToString("00") : dt.Day.ToString(CultureInfo.InvariantCulture);
        string m = ZeroPaddingDate ? dt.Month.ToString("00") : dt.Month.ToString(CultureInfo.InvariantCulture);
        string y = dt.Year.ToString("0000");
        var sep = DateDelimiter;
        var date = DateOrder switch
        {
            DateOrder.MDY => $"{m}{sep}{d}{sep}{y}",
            DateOrder.YMD => $"{y}{sep}{m}{sep}{d}",
            _ => $"{d}{sep}{m}{sep}{y}",
        };
        if (dt.TimeOfDay != TimeSpan.Zero)
        {
            var td = TimeDelimiter;
            date += $" {dt.Hour:00}{td}{dt.Minute:00}{td}{dt.Second:00}";
        }
        return date;
    }
}

/// <summary>Exports a DataView's rows (respecting its filter/sort) to a file in various formats.</summary>
public static class ExportService
{
    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Dbf => "dbf",
        ExportFormat.Txt => "txt",
        ExportFormat.Csv => "csv",
        ExportFormat.Tsv => "tsv",
        ExportFormat.Html => "html",
        ExportFormat.Xls => "xls",
        ExportFormat.Xlsx => "xlsx",
        ExportFormat.Sql => "sql",
        ExportFormat.Xml => "xml",
        ExportFormat.Json => "json",
        _ => "txt"
    };

    /// <summary>Human label with extension, for the format picker.</summary>
    public static string Label(ExportFormat f) => f switch
    {
        ExportFormat.Dbf => "DBase file (*.dbf)",
        ExportFormat.Txt => "Text file (*.txt)",
        ExportFormat.Csv => "CSV file (*.csv)",
        ExportFormat.Tsv => "Tab-separated (*.tsv)",
        ExportFormat.Html => "HTML file (*.html)",
        ExportFormat.Xls => "Excel 97-2003 (*.xls)",
        ExportFormat.Xlsx => "Excel file (*.xlsx)",
        ExportFormat.Sql => "SQL script (*.sql)",
        ExportFormat.Xml => "XML file (*.xml)",
        ExportFormat.Json => "JSON file (*.json)",
        _ => f.ToString()
    };

    /// <param name="display">Optional per-cell display override (e.g. Clarion date/time); null = use raw value.</param>
    /// <param name="objectName">Source table name, used as the target table for SQL export.</param>
    public static void Export(DataView view, IReadOnlyList<string> columns, ExportFormat format, string path,
        bool includeHeaders, Func<string, object?, string?>? display = null, string? objectName = null,
        ExportOptions? options = null)
    {
        var rows = view.Cast<DataRowView>().ToList();
        options ??= ExportOptions.Default;

        switch (format)
        {
            case ExportFormat.Csv: WriteDelimited(path, columns, rows, ',', includeHeaders, display); break;
            case ExportFormat.Tsv: WriteDelimited(path, columns, rows, '\t', includeHeaders, display); break;
            case ExportFormat.Txt: WriteDelimited(path, columns, rows, '\t', includeHeaders, display); break;
            case ExportFormat.Json: WriteJson(path, columns, rows, display, options); break;
            case ExportFormat.Xml: WriteXml(path, columns, rows, display); break;
            case ExportFormat.Html: WriteHtml(path, columns, rows, includeHeaders, display); break;
            case ExportFormat.Xlsx: WriteXlsx(path, columns, rows, includeHeaders, display); break;
            case ExportFormat.Xls: WriteXls(path, columns, rows, includeHeaders, display); break;
            case ExportFormat.Dbf: WriteDbf(path, columns, rows, display); break;
            case ExportFormat.Sql: WriteSql(path, columns, rows, objectName ?? "exported_data", display, options); break;
        }
    }

    private static string Cell(DataRowView row, string col, Func<string, object?, string?>? display)
    {
        var raw = row[col];
        var over = display?.Invoke(col, raw is DBNull ? null : raw);
        if (over is not null) return over;
        return raw is DBNull ? "" : raw.ToString() ?? "";
    }

    private static void WriteDelimited(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        char delim, bool headers, Func<string, object?, string?>? display)
    {
        var sb = new StringBuilder();
        string Q(string v) =>
            v.IndexOf(delim) >= 0 || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
                ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        if (headers) sb.AppendLine(string.Join(delim, cols.Select(Q)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(delim, cols.Select(c => Q(Cell(r, c, display)))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteJson(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        Func<string, object?, string?>? display, ExportOptions opt)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        if (opt.JsonLegacyRecordsKey) { w.WriteStartObject(); w.WriteStartArray("RECORDS"); }
        else w.WriteStartArray();

        foreach (var r in rows)
        {
            w.WriteStartObject();
            foreach (var c in cols)
            {
                var raw = r[c];
                var over = display?.Invoke(c, raw is DBNull ? null : raw);
                if (over is not null) { w.WriteString(c, over); continue; }
                WriteJsonValue(w, c, raw, opt);
            }
            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (opt.JsonLegacyRecordsKey) w.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter w, string name, object value, ExportOptions opt)
    {
        switch (value)
        {
            case null or DBNull: w.WriteNull(name); break;
            case bool b: w.WriteBoolean(name, b); break;
            case byte or sbyte or short or ushort or int or uint or long:
                w.WriteNumber(name, Convert.ToInt64(value)); break;
            case ulong ul: w.WriteNumber(name, ul); break;
            case float or double or decimal:
                // JSON numbers require '.'; honor a custom decimal symbol by emitting a string.
                if (opt.DecimalSymbol == ".") w.WriteNumber(name, Convert.ToDecimal(value));
                else w.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(".", opt.DecimalSymbol));
                break;
            case DateTime dt: w.WriteString(name, opt.FormatDate(dt)); break;
            case Guid g: w.WriteString(name, g.ToString()); break;
            case byte[] bytes:
                w.WriteString(name, opt.Binary == BinaryEncoding.Hex
                    ? "0x" + Convert.ToHexString(bytes)
                    : Convert.ToBase64String(bytes));
                break;
            default: w.WriteString(name, value.ToString()); break;
        }
    }

    private static void WriteXml(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        Func<string, object?, string?>? display)
    {
        var root = new XElement("rows");
        foreach (var r in rows)
        {
            var rowEl = new XElement("row");
            foreach (var c in cols)
                rowEl.Add(new XElement(XmlName(c), Cell(r, c, display)));
            root.Add(rowEl);
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    private static string XmlName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static void WriteHtml(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        bool headers, Func<string, object?, string?>? display)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                      "table{border-collapse:collapse;font-family:Segoe UI,Arial,sans-serif;font-size:13px}" +
                      "th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}th{background:#f1f4f7}" +
                      "</style></head><body><table>");
        if (headers)
            sb.Append("<tr>").Append(string.Concat(cols.Select(c => $"<th>{WebUtility.HtmlEncode(c)}</th>"))).AppendLine("</tr>");
        foreach (var r in rows)
            sb.Append("<tr>").Append(string.Concat(cols.Select(c => $"<td>{WebUtility.HtmlEncode(Cell(r, c, display))}</td>"))).AppendLine("</tr>");
        sb.AppendLine("</table></body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteXlsx(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        bool headers, Func<string, object?, string?>? display)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        var row = 1;

        if (headers)
        {
            for (var c = 0; c < cols.Count; c++)
                ws.Cell(row, c + 1).Value = cols[c];
            ws.Row(row).Style.Font.Bold = true;
            row++;
        }

        foreach (var r in rows)
        {
            for (var c = 0; c < cols.Count; c++)
            {
                var raw = r[cols[c]];
                var over = display?.Invoke(cols[c], raw is DBNull ? null : raw);
                var cell = ws.Cell(row, c + 1);
                if (over is not null) cell.Value = over;
                else SetXlsxCell(cell, raw);
            }
            row++;
        }

        ws.Columns().AdjustToContents();
        if (headers) ws.SheetView.FreezeRows(1);
        wb.SaveAs(path);
    }

    private static void SetXlsxCell(IXLCell cell, object value)
    {
        switch (value)
        {
            case null or DBNull: break;
            case bool b: cell.Value = b; break;
            case byte or sbyte or short or ushort or int or uint or long or ulong
                 or float or double or decimal: cell.Value = Convert.ToDouble(value); break;
            case DateTime dt: cell.Value = dt; break;
            default: cell.Value = value.ToString(); break;
        }
    }

    // ---- Excel 97-2003 (.xls, BIFF8 via NPOI) ---------------------------
    private static void WriteXls(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        bool headers, Func<string, object?, string?>? display)
    {
        var wb = new HSSFWorkbook();
        var sheet = wb.CreateSheet("Data");

        var boldFont = wb.CreateFont();
        boldFont.IsBold = true;
        var headStyle = wb.CreateCellStyle();
        headStyle.SetFont(boldFont);
        var dateStyle = wb.CreateCellStyle();
        dateStyle.DataFormat = wb.CreateDataFormat().GetFormat("yyyy-mm-dd hh:mm:ss");

        var rowIdx = 0;
        if (headers)
        {
            var hr = sheet.CreateRow(rowIdx++);
            for (var c = 0; c < cols.Count; c++)
            {
                var cell = hr.CreateCell(c);
                cell.SetCellValue(cols[c]);
                cell.CellStyle = headStyle;
            }
        }

        foreach (var r in rows)
        {
            var xr = sheet.CreateRow(rowIdx++);
            for (var c = 0; c < cols.Count; c++)
            {
                var cell = xr.CreateCell(c);
                var raw = r[cols[c]];
                var over = display?.Invoke(cols[c], raw is DBNull ? null : raw);
                if (over is not null) { cell.SetCellValue(over); continue; }
                switch (raw)
                {
                    case null or DBNull: break;
                    case bool b: cell.SetCellValue(b); break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong
                         or float or double or decimal: cell.SetCellValue(Convert.ToDouble(raw)); break;
                    case DateTime dt: cell.SetCellValue(dt); cell.CellStyle = dateStyle; break;
                    default: cell.SetCellValue(raw.ToString()); break;
                }
            }
        }

        for (var c = 0; c < cols.Count; c++) sheet.AutoSizeColumn(c);
        using var fs = File.Create(path);
        wb.Write(fs);
    }

    // ---- SQL script (INSERT statements) ---------------------------------
    private static void WriteSql(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        string tableName, Func<string, object?, string?>? display, ExportOptions opt)
    {
        var table   = "[" + tableName.Replace("]", "]]") + "]";
        var colList = string.Join(", ", cols.Select(c => "[" + c.Replace("]", "]]") + "]"));
        var sb = new StringBuilder();

        sb.Append("-- Export of ").Append(tableName)
          .Append(" — ").Append(rows.Count).AppendLine(" row(s)")
          .Append("-- Generated ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
          .AppendLine();

        if (opt.SqlNoCount)
            sb.AppendLine("SET NOCOUNT ON;").AppendLine();

        var batchSize = opt.SqlBatchSize > 0 ? opt.SqlBatchSize : int.MaxValue;

        for (var idx = 0; idx < rows.Count; idx++)
        {
            var r = rows[idx];
            sb.Append("INSERT INTO ").Append(table).Append(" (").Append(colList).Append(") VALUES (");
            for (var i = 0; i < cols.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var raw = r[cols[i]];
                var over = display?.Invoke(cols[i], raw is DBNull ? null : raw);
                sb.Append(over is not null ? SqlLiteral(over) : SqlValue(raw));
            }
            sb.AppendLine(");");

            // Emit GO every batchSize rows (1-based: after row 100, 200, …)
            if (opt.SqlBatchSize > 0 && (idx + 1) % batchSize == 0 && idx + 1 < rows.Count)
                sb.AppendLine().AppendLine("GO").AppendLine();
        }

        if (opt.SqlBatchSize > 0)
            sb.AppendLine().AppendLine("GO");

        if (opt.SqlNoCount)
            sb.AppendLine().AppendLine("SET NOCOUNT OFF;");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string SqlLiteral(string s) => "'" + s.Replace("'", "''") + "'";

    private static string SqlValue(object value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "1" : "0",
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        float or double => Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
        Guid g => "'" + g + "'",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => SqlLiteral(value.ToString() ?? "")
    };

    // ---- dBASE III+ (.dbf) ----------------------------------------------
    private static void WriteDbf(string path, IReadOnlyList<string> cols, List<DataRowView> rows,
        Func<string, object?, string?>? display)
    {
        var enc = Encoding.Latin1; // DBF is single-byte ANSI; Latin1 is built-in and close to cp1252.
        var fields = PlanDbfFields(cols, rows, display);
        var recordLen = 1 + fields.Sum(f => f.Length); // 1 = deletion flag

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, enc);

        // Header (32 bytes) + field descriptors (32 each) + terminator (1).
        var headerLen = 32 + fields.Count * 32 + 1;
        var now = DateTime.Now;
        w.Write((byte)0x03);                       // dBASE III without memo
        w.Write((byte)(now.Year % 100));
        w.Write((byte)now.Month);
        w.Write((byte)now.Day);
        w.Write((uint)rows.Count);                 // record count
        w.Write((ushort)headerLen);
        w.Write((ushort)recordLen);
        w.Write(new byte[20]);                     // reserved

        foreach (var f in fields)
        {
            var name = new byte[11];
            var nb = enc.GetBytes(f.Name);
            Array.Copy(nb, name, Math.Min(nb.Length, 10));
            w.Write(name);
            w.Write((byte)f.Type);
            w.Write(new byte[4]);                  // field data address
            w.Write((byte)f.Length);
            w.Write((byte)f.Decimals);
            w.Write(new byte[14]);                 // reserved
        }
        w.Write((byte)0x0D);                       // header terminator

        foreach (var r in rows)
        {
            w.Write((byte)0x20);                   // not deleted
            foreach (var f in fields)
            {
                var raw = r[f.Source];
                var over = display?.Invoke(f.Source, raw is DBNull ? null : raw);
                w.Write(FormatDbfField(f, raw, over, enc));
            }
        }
        w.Write((byte)0x1A);                       // EOF marker
    }

    private sealed record DbfField(string Source, string Name, char Type, int Length, int Decimals);

    private static List<DbfField> PlanDbfFields(IReadOnlyList<string> cols, List<DataRowView> rows,
        Func<string, object?, string?>? display)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<DbfField>();
        foreach (var col in cols)
        {
            // Decide type from the column, sizing from the widest formatted value.
            var type = 'C';
            var dec = 0;
            var maxLen = 1;
            // Inspect non-null values to classify.
            char Classify(object? v) => v switch
            {
                bool => 'L',
                DateTime => 'D',
                byte or sbyte or short or ushort or int or uint or long or ulong => 'N',
                float or double or decimal => 'F',
                _ => 'C'
            };
            var cls = 'C';
            foreach (DataRowView r in rows)
            {
                var v = r[col];
                if (v is null or DBNull) continue;
                cls = Classify(v);
                break;
            }
            switch (cls)
            {
                case 'L': type = 'L'; maxLen = 1; break;
                case 'D': type = 'D'; maxLen = 8; break;
                case 'N': type = 'N'; dec = 0; break;
                case 'F': type = 'N'; dec = 6; break;
                default: type = 'C'; break;
            }

            // Width from widest formatted value (display override wins).
            foreach (DataRowView r in rows)
            {
                var raw = r[col];
                var over = display?.Invoke(col, raw is DBNull ? null : raw);
                var s = over ?? DbfText(type, dec, raw);
                if (s.Length > maxLen) maxLen = s.Length;
            }
            var len = type switch
            {
                'L' => 1,
                'D' => 8,
                'N' => Math.Clamp(maxLen, 1, 20),
                _ => Math.Clamp(maxLen, 1, 254)
            };
            if (type == 'N' && dec > 0 && len < dec + 2) len = dec + 2;

            fields.Add(new DbfField(col, UniqueDbfName(col, used), type, len, type == 'N' ? dec : 0));
        }
        return fields;
    }

    private static string UniqueDbfName(string col, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var ch in col.ToUpperInvariant())
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
        if (sb.Length == 0) sb.Append('F');
        var baseName = sb.ToString();
        if (baseName.Length > 10) baseName = baseName[..10];
        var name = baseName;
        var n = 1;
        while (!used.Add(name))
        {
            var suffix = (++n).ToString();
            name = baseName[..Math.Min(baseName.Length, 10 - suffix.Length)] + suffix;
        }
        return name;
    }

    private static string DbfText(char type, int dec, object? raw) => raw switch
    {
        null or DBNull => "",
        bool b => b ? "T" : "F",
        DateTime dt => dt.ToString("yyyyMMdd"),
        _ when type == 'N' => Convert.ToDecimal(raw, CultureInfo.InvariantCulture)
                                     .ToString(dec > 0 ? "F" + dec : "F0", CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? ""
    };

    private static byte[] FormatDbfField(DbfField f, object? raw, string? over, Encoding enc)
    {
        string text;
        if (over is not null && f.Type == 'C') text = over;
        else text = DbfText(f.Type, f.Decimals, raw);

        // Numbers are right-aligned; everything else left-aligned. All space-padded to width.
        if (text.Length > f.Length) text = text[..f.Length];
        text = f.Type == 'N' ? text.PadLeft(f.Length) : text.PadRight(f.Length);
        var bytes = enc.GetBytes(text);
        if (bytes.Length != f.Length)
        {
            var fixedBytes = new byte[f.Length];
            Array.Fill(fixedBytes, (byte)0x20);
            Array.Copy(bytes, fixedBytes, Math.Min(bytes.Length, f.Length));
            return fixedBytes;
        }
        return bytes;
    }
}
