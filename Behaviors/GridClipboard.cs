using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DataPortStudio.Services;
using DataPortStudio.ViewModels;
using DataPortStudio.Views;

namespace DataPortStudio.Behaviors;

/// <summary>
/// Spreadsheet-friendly clipboard copy for the data grid. Writes HTML (a real table),
/// tab-separated text, and CSV — all properly quoted — so Excel and Google Sheets paste
/// each value into its own cell even when a value contains tabs or line breaks.
/// Clarion date/time columns are copied as their displayed value.
/// </summary>
public static class GridClipboard
{
    public static void Copy(DataGrid grid, bool includeHeaders)
    {
        var (headers, rows) = Gather(grid);
        if (rows.Count == 0) return;

        try
        {
            var data = new DataObject();
            data.SetText(BuildDelimited(headers, rows, '\t', includeHeaders), TextDataFormat.UnicodeText);
            data.SetData(DataFormats.CommaSeparatedValue, BuildDelimited(headers, rows, ',', includeHeaders));
            data.SetData(DataFormats.Html, BuildCfHtml(BuildHtmlTable(headers, rows, includeHeaders)));
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            // Clipboard can be transiently locked by another app — ignore.
        }
    }

    private static (List<string> Headers, List<List<string>> Rows) Gather(DataGrid grid)
    {
        var tab = grid.DataContext as TableTabViewModel;

        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        // If individual cells are selected, restrict to those columns.
        if (grid.SelectedCells.Count > 0)
        {
            var selectedCols = new HashSet<DataGridColumn>(grid.SelectedCells.Select(c => c.Column));
            columns = columns.Where(selectedCols.Contains).ToList();
        }

        var headers = columns.Select(GetColumnName).ToList();

        var selectedRows = grid.SelectedCells.Count > 0
            ? new HashSet<object>(grid.SelectedCells.Select(c => c.Item))
            : new HashSet<object>(grid.SelectedItems.Cast<object>());

        var rows = new List<List<string>>();
        foreach (var item in grid.Items) // preserves display order
        {
            if (!selectedRows.Contains(item) || item is not DataRowView rowView) continue;

            var cells = new List<string>(columns.Count);
            foreach (var col in columns)
            {
                var name = GetColumnName(col);
                object? raw = !string.IsNullOrEmpty(name) && rowView.Row.Table.Columns.Contains(name)
                    ? rowView[name]
                    : null;
                cells.Add(FormatValue(tab, name, raw));
            }
            rows.Add(cells);
        }

        return (headers, rows);
    }

    private static string FormatValue(TableTabViewModel? tab, string name, object? raw)
    {
        if (raw is null || raw == DBNull.Value) return "";

        var kind = tab?.GetEffectiveKind(name);
        if (kind == ClarionKind.Date && TryLong(raw, out var d))
        {
            var date = ClarionDate.FromClarion(d);
            if (date is not null) return date.Value.ToString("yyyy-MM-dd");
        }
        if (kind == ClarionKind.Time && TryLong(raw, out var t))
        {
            var s = ClarionTime.Format(t);
            if (s is not null) return s;
        }
        return raw.ToString() ?? "";
    }

    private static string BuildDelimited(List<string> headers, List<List<string>> rows, char delim, bool includeHeaders)
    {
        var sb = new StringBuilder();
        if (includeHeaders)
            sb.Append(string.Join(delim, headers.Select(h => Field(h, delim)))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(delim, row.Select(c => Field(c, delim)))).Append("\r\n");
        return sb.ToString();
    }

    private static string Field(string value, char delim)
    {
        if (value.IndexOf(delim) >= 0 || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string BuildHtmlTable(List<string> headers, List<List<string>> rows, bool includeHeaders)
    {
        var sb = new StringBuilder();
        sb.Append("<table>");
        if (includeHeaders)
        {
            sb.Append("<tr>");
            foreach (var h in headers) sb.Append("<th>").Append(Encode(h)).Append("</th>");
            sb.Append("</tr>");
        }
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var c in row) sb.Append("<td>").Append(Encode(c)).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Encode(string value) =>
        WebUtility.HtmlEncode(value).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    /// <summary>Wraps an HTML fragment in the CF_HTML clipboard format with byte offsets.</summary>
    private static string BuildCfHtml(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:00000000}\r\nEndHTML:{1:00000000}\r\nStartFragment:{2:00000000}\r\nEndFragment:{3:00000000}\r\n";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";

        var headerLength = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(pre);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(post);

        return string.Format(header, headerLength, endHtml, startFragment, endFragment) + pre + fragment + post;
    }

    private static string GetColumnName(DataGridColumn column)
    {
        if (column is DataGridBoundColumn { Binding: Binding b } && !string.IsNullOrEmpty(b.Path?.Path))
            return b.Path.Path;
        return column.SortMemberPath ?? "";
    }

    private static bool TryLong(object value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case decimal d when d == Math.Truncate(d): result = (long)d; return true;
            default: result = 0; return false;
        }
    }

    // ===================== PASTE / FILL =====================

    /// <summary>
    /// Pastes clipboard data. A single value fills every selected cell; a block is pasted
    /// from the top-left of the selection (or the current cell), adding rows past the end.
    /// </summary>
    public static void Paste(DataGrid grid)
    {
        var text = GetClipboardText();
        if (string.IsNullOrEmpty(text)) return;
        if (grid.ItemsSource is not DataView view || view.Table is not { } table) return;

        var matrix = ParseMatrix(text);
        if (matrix.Count == 0) return;

        var tab = grid.DataContext as TableTabViewModel;
        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();
        if (columns.Count == 0) return;

        var singleValue = matrix.Count == 1 && matrix[0].Count == 1;
        if (singleValue && grid.SelectedCells.Count > 1)
        {
            FillSelectedCells(grid, matrix[0][0]);
            return;
        }

        var (anchorRow, anchorCol) = ResolveAnchor(grid, view, columns);
        int applied = 0, skipped = 0;
        string? firstError = null;

        for (var r = 0; r < matrix.Count; r++)
        {
            var viewIndex = anchorRow + r;
            var isNew = viewIndex >= view.Count;
            DataRowView targetRow;
            try
            {
                targetRow = isNew ? view.AddNew()! : view[viewIndex];
            }
            catch (Exception ex) { skipped++; firstError ??= ex.Message; continue; }

            try
            {
                var rowVals = matrix[r];
                for (var c = 0; c < rowVals.Count; c++)
                {
                    var colIndex = anchorCol + c;
                    if (colIndex >= columns.Count) break;
                    SetCellValue(table, tab, targetRow, columns[colIndex], rowVals[c]);
                }
                targetRow.EndEdit(); // commits an added row; no-op for an existing one
                applied++;
            }
            catch (Exception ex)
            {
                // A row that can't satisfy constraints (e.g. a new row missing a required key)
                // is rolled back so the rest of the paste still applies.
                skipped++;
                firstError ??= ex.Message;
                try { targetRow.CancelEdit(); } catch { /* ignore */ }
            }
        }

        if (skipped > 0)
            Dialogs.ShowError("Paste partially applied",
                $"{applied} row(s) pasted, {skipped} skipped.\n\n" +
                $"{firstError}\n\n" +
                "New rows are skipped when a required column (such as a primary key not covered " +
                "by the pasted data) would be left empty.");
    }

    /// <summary>Sets every currently-selected cell to the same value (paste-fill).</summary>
    public static void FillSelectedCells(DataGrid grid, string value)
    {
        if (grid.ItemsSource is not DataView view || view.Table is not { } table) return;
        var tab = grid.DataContext as TableTabViewModel;
        foreach (var cell in grid.SelectedCells)
            SetCellValue(table, tab, cell.Item as DataRowView, cell.Column, value);
    }

    /// <summary>Sets a captured list of cells to the same value (type-fill across a selection).</summary>
    public static void FillCells(DataGrid grid, IEnumerable<(DataRowView Row, DataGridColumn Col)> cells, string value)
    {
        if (grid.ItemsSource is not DataView view || view.Table is not { } table) return;
        var tab = grid.DataContext as TableTabViewModel;
        foreach (var (row, col) in cells)
            SetCellValue(table, tab, row, col, value);
    }

    private static (int Row, int Col) ResolveAnchor(DataGrid grid, DataView view, List<DataGridColumn> columns)
    {
        var row = int.MaxValue;
        var col = int.MaxValue;
        foreach (var cell in grid.SelectedCells)
        {
            var ri = cell.Item is DataRowView rv ? IndexOfRow(view, rv) : -1;
            var ci = columns.IndexOf(cell.Column);
            if (ri >= 0 && ri < row) row = ri;
            if (ci >= 0 && ci < col) col = ci;
        }

        if (row == int.MaxValue)
        {
            row = grid.CurrentCell.Item is DataRowView crv ? IndexOfRow(view, crv) : view.Count;
            if (row < 0) row = view.Count;
        }
        if (col == int.MaxValue)
            col = grid.CurrentCell.Column is { } cc ? Math.Max(0, columns.IndexOf(cc)) : 0;

        return (row, col);
    }

    private static int IndexOfRow(DataView view, DataRowView target)
    {
        for (var i = 0; i < view.Count; i++)
            if (ReferenceEquals(view[i].Row, target.Row)) return i;
        return -1;
    }

    /// <summary>Public entry for live type-fill (writes one cell's value).</summary>
    public static void SetCellValuePublic(DataTable table, TableTabViewModel? tab,
        DataRowView? row, DataGridColumn? column, string text)
        => SetCellValue(table, tab, row, column, text);

    private static void SetCellValue(DataTable table, TableTabViewModel? tab,
        DataRowView? row, DataGridColumn? column, string text)
    {
        if (row is null || column is null) return;
        var name = GetColumnName(column);
        if (string.IsNullOrEmpty(name) || !table.Columns.Contains(name)) return;

        var col = table.Columns[name]!;
        if (col.ReadOnly || col.AutoIncrement) return;

        try { row[name] = ConvertForColumn(tab, name, col, text); }
        catch { /* value not compatible with this column — skip it */ }
    }

    private static object ConvertForColumn(TableTabViewModel? tab, string name, DataColumn col, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (col.AllowDBNull) return DBNull.Value;
            return col.DataType == typeof(string) ? "" : DBNull.Value;
        }

        var kind = tab?.GetEffectiveKind(name);
        if (kind == ClarionKind.Date &&
            (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var cdt) ||
             DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out cdt)))
            return Convert.ChangeType(ClarionDate.ToClarion(cdt), col.DataType);

        if (kind == ClarionKind.Time && ClarionTime.TryParse(text, out var clarion))
            return Convert.ChangeType(clarion, col.DataType);

        var type = col.DataType;
        if (type == typeof(string)) return text;

        // Types that Convert.ChangeType can't handle from a string.
        if (type == typeof(Guid)) return Guid.Parse(text.Trim());
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(text.Trim(), CultureInfo.CurrentCulture);
        if (type == typeof(bool)) return ParseBool(text);
        if (type == typeof(DateTime))
        {
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt;
            throw new FormatException($"'{text}' is not a valid date/time.");
        }
        if (type == typeof(byte[]))
            return Convert.FromBase64String(text.Trim());

        return Convert.ChangeType(text, type, CultureInfo.CurrentCulture);
    }

    private static bool ParseBool(string text)
    {
        var s = text.Trim();
        if (bool.TryParse(s, out var b)) return b;
        return s.ToLowerInvariant() switch
        {
            "1" or "y" or "yes" or "t" or "true" or "si" or "sí" or "verdadero" => true,
            "0" or "n" or "no" or "f" or "false" or "falso" => false,
            _ => Convert.ToBoolean(s, CultureInfo.CurrentCulture)
        };
    }

    private static string GetClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) return Clipboard.GetText(TextDataFormat.UnicodeText);
            if (Clipboard.ContainsText(TextDataFormat.Text)) return Clipboard.GetText(TextDataFormat.Text);
        }
        catch { /* clipboard busy */ }
        return "";
    }

    /// <summary>Parses tab-delimited clipboard text into a matrix, honoring quoted fields.</summary>
    private static List<List<string>> ParseMatrix(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case '\t': row.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break;
                    case '\n': row.Add(sb.ToString()); sb.Clear(); rows.Add(row); row = new List<string>(); break;
                    default: sb.Append(ch); break;
                }
            }
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }

        return rows;
    }
}
