using ClosedXML.Excel;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DataPortStudio.Services;

/// <summary>
/// Reads and writes the Excel half of a folder connection: each .xls/.xlsx file's worksheets
/// appear as tables. Sheet name is stored in Node.Schema; file name (e.g. "Sales.xlsx") in
/// Node.Database. Folder scanning and the other file formats live in <see cref="TabularFileService"/>,
/// which is what the rest of the app calls.
/// </summary>
public static class ExcelService
{
    private static bool IsXlsx(string ext) =>
        ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);

    private static bool IsXls(string ext) =>
        ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns sheet names for a single Excel file (in workbook order).</summary>
    public static List<string> ListSheetsForFile(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var ext = Path.GetExtension(path);
        try
        {
            if (IsXlsx(ext))
            {
                var fast = TryListXlsxSheetNames(path);
                if (fast is not null) return fast;
                using var wb = new XLWorkbook(path);
                return wb.Worksheets.Select(ws => ws.Name).ToList();
            }
            if (IsXls(ext))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var wb = new HSSFWorkbook(fs);
                return Enumerable.Range(0, wb.NumberOfSheets).Select(i => wb.GetSheetName(i)).ToList();
            }
        }
        catch { /* corrupt / locked file */ }
        return [];
    }

    /// <summary>
    /// Reads sheet names straight out of the workbook part inside the .xlsx zip. Opening the file
    /// with ClosedXML parses every cell of every sheet, which is far too slow when all we want is
    /// the list of sheet names for a folder full of workbooks. Returns null if the shortcut does
    /// not apply, so the caller can fall back to ClosedXML.
    /// </summary>
    private static List<string>? TryListXlsxSheetNames(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("xl/workbook.xml")
                        ?? zip.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("workbook.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            var names = document.Descendants()
                .Where(e => e.Name.LocalName == "sheets")
                .Elements()
                .Where(e => e.Name.LocalName == "sheet")
                .Select(e => e.Attribute("name")?.Value)
                .OfType<string>()
                .ToList();
            return names.Count > 0 ? names : null;
        }
        catch { return null; }
    }

    /// <summary>Reads one worksheet into a DataTable. First row = column headers. All values are strings.</summary>
    public static DataTable ReadTable(string folder, string fileName, string sheetName, int rowLimit)
    {
        var path = Path.Combine(folder, fileName);
        var ext = Path.GetExtension(path);
        if (IsXlsx(ext)) return ReadXlsx(path, sheetName, rowLimit);
        if (IsXls(ext)) return ReadXls(path, sheetName, rowLimit);
        throw new NotSupportedException($"Unsupported file format: {ext}");
    }

    private static DataTable ReadXlsx(string path, string sheetName, int rowLimit)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w =>
                     w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException(
                     $"Sheet '{sheetName}' not found in {Path.GetFileName(path)}.");

        var table = new DataTable(sheetName);
        var range = ws.RangeUsed();
        if (range == null) return table;

        var rows = range.Rows().ToList();
        if (rows.Count == 0) return table;

        int colCount = range.ColumnCount();

        // First row = headers
        var headerRow = rows[0];
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= colCount; c++)
        {
            var name = headerRow.Cell(c).GetString().Trim();
            if (string.IsNullOrEmpty(name)) name = $"Column{c}";
            if (!usedNames.Add(name))
            {
                int n = 2;
                while (!usedNames.Add($"{name}_{n}")) n++;
                name = $"{name}_{n - 1}";
            }
            table.Columns.Add(name, typeof(string));
        }

        int count = 0;
        for (int r = 1; r < rows.Count; r++)
        {
            if (rowLimit > 0 && count >= rowLimit) break;
            var row = rows[r];

            bool anyValue = false;
            for (int c = 1; c <= colCount; c++)
                if (!row.Cell(c).IsEmpty()) { anyValue = true; break; }
            if (!anyValue) continue;

            var dr = table.NewRow();
            for (int c = 1; c <= colCount; c++)
            {
                var cell = row.Cell(c);
                dr[c - 1] = cell.IsEmpty() ? DBNull.Value : (object)FormatXlsxCell(cell);
            }
            table.Rows.Add(dr);
            count++;
        }

        table.AcceptChanges();
        return table;
    }

    private static string FormatXlsxCell(IXLCell cell)
    {
        try
        {
            return cell.DataType switch
            {
                XLDataType.Text => cell.GetString(),
                XLDataType.Number => cell.GetDouble().ToString(),
                XLDataType.DateTime => FormatDate(cell.GetDateTime()),
                XLDataType.TimeSpan => cell.GetTimeSpan().ToString(@"hh\:mm\:ss"),
                XLDataType.Boolean => cell.GetBoolean() ? "TRUE" : "FALSE",
                _ => cell.GetString()
            };
        }
        catch { return ""; }
    }

    private static string FormatDate(DateTime dt) =>
        dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd")
            : dt.ToString("yyyy-MM-dd HH:mm:ss");

    private static DataTable ReadXls(string path, string sheetName, int rowLimit)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var wb = new HSSFWorkbook(fs);
        var sheet = wb.GetSheet(sheetName)
                    ?? throw new InvalidOperationException(
                        $"Sheet '{sheetName}' not found in {Path.GetFileName(path)}.");

        var table = new DataTable(sheetName);
        var headerRow = sheet.GetRow(sheet.FirstRowNum);
        if (headerRow == null) return table;

        int colCount = headerRow.LastCellNum;
        if (colCount <= 0) return table;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < colCount; c++)
        {
            var name = headerRow.GetCell(c)?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) name = $"Column{c + 1}";
            if (!usedNames.Add(name))
            {
                int n = 2;
                while (!usedNames.Add($"{name}_{n}")) n++;
                name = $"{name}_{n - 1}";
            }
            table.Columns.Add(name, typeof(string));
        }

        int count = 0;
        for (int r = sheet.FirstRowNum + 1; r <= sheet.LastRowNum; r++)
        {
            if (rowLimit > 0 && count >= rowLimit) break;
            var row = sheet.GetRow(r);
            if (row == null) continue;

            bool anyValue = false;
            var dr = table.NewRow();
            for (int c = 0; c < colCount; c++)
            {
                var cell = row.GetCell(c);
                if (cell == null || cell.CellType == CellType.Blank)
                {
                    dr[c] = DBNull.Value;
                    continue;
                }
                var val = FormatXlsCell(cell);
                dr[c] = string.IsNullOrEmpty(val) ? (object)DBNull.Value : val;
                anyValue = true;
            }
            if (!anyValue) continue;
            table.Rows.Add(dr);
            count++;
        }

        table.AcceptChanges();
        return table;
    }

    private static string FormatXlsCell(ICell cell)
    {
        try
        {
            if (cell.CellType == CellType.Formula)
            {
                var cached = cell.CachedFormulaResultType;
                if (cached == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
                    return TryFormatXlsDate(cell);
                if (cached == CellType.Numeric) return cell.NumericCellValue.ToString();
                if (cached == CellType.Boolean) return cell.BooleanCellValue ? "TRUE" : "FALSE";
                return cell.StringCellValue ?? "";
            }
            return cell.CellType switch
            {
                CellType.Numeric when DateUtil.IsCellDateFormatted(cell) => TryFormatXlsDate(cell),
                CellType.Numeric => cell.NumericCellValue.ToString(),
                CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
                _ => cell.ToString() ?? ""
            };
        }
        catch { return ""; }
    }

    private static string TryFormatXlsDate(ICell cell)
    {
        try
        {
            var dt = cell.DateCellValue;
            return dt.HasValue ? FormatDate(dt.Value) : cell.NumericCellValue.ToString();
        }
        catch { return cell.NumericCellValue.ToString(); }
    }

    /// <summary>
    /// Writes the DataTable back to the worksheet, replacing all data rows (row 1 = headers stays untouched).
    /// Rows with DataRowState.Deleted are omitted; all others are written with their current values.
    /// </summary>
    public static void SaveTable(string folder, string fileName, string sheetName, DataTable table)
    {
        var path = Path.Combine(folder, fileName);
        var ext = Path.GetExtension(path);
        if (IsXlsx(ext)) SaveXlsx(path, sheetName, table);
        else if (IsXls(ext)) SaveXls(path, sheetName, table);
        else throw new NotSupportedException($"Unsupported file format: {ext}");
    }

    private static string CellValue(DataRow row, int colIndex)
    {
        var v = row[colIndex];
        return (v == null || v == DBNull.Value) ? "" : v.ToString()!;
    }

    private static void SaveXlsx(string path, string sheetName, DataTable table)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w =>
                     w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException(
                     $"Sheet '{sheetName}' not found in {Path.GetFileName(path)}.");

        // Delete all data rows (bottom-up so row numbers stay valid during deletion)
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = lastRow; r >= 2; r--)
            ws.Row(r).Delete();

        // Write all non-deleted rows
        int rowNum = 2;
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            for (int c = 0; c < table.Columns.Count; c++)
                ws.Cell(rowNum, c + 1).SetValue(CellValue(row, c));
            rowNum++;
        }

        wb.Save();
    }

    private static void SaveXls(string path, string sheetName, DataTable table)
    {
        HSSFWorkbook wb;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            wb = new HSSFWorkbook(fs);

        var sheet = wb.GetSheet(sheetName)
                    ?? throw new InvalidOperationException(
                        $"Sheet '{sheetName}' not found in {Path.GetFileName(path)}.");

        // Remove all data rows (keep header row at FirstRowNum)
        for (int r = sheet.LastRowNum; r > sheet.FirstRowNum; r--)
        {
            var row = sheet.GetRow(r);
            if (row != null) sheet.RemoveRow(row);
        }

        // Write all non-deleted rows
        int rowNum = sheet.FirstRowNum + 1;
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            var excelRow = sheet.CreateRow(rowNum++);
            for (int c = 0; c < table.Columns.Count; c++)
                excelRow.CreateCell(c).SetCellValue(CellValue(row, c));
        }

        using var outFs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(outFs);
    }

    /// <summary>Structure / info panel for an Excel sheet.</summary>
    public static Task<TableStructure> GetStructureAsync(
        string folder, string fileName, string sheetName, string connectionName = "")
    {
        var path = Path.Combine(folder, fileName);
        var fi = new FileInfo(path);

        const int w = -18;
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(connectionName)) info.AppendLine($"{"Connection",w}{connectionName}");
        info.AppendLine($"{"File",w}{fi.Name}");
        info.AppendLine($"{"Folder",w}{folder}");
        info.AppendLine($"{"Sheet",w}{sheetName}");
        if (fi.Exists)
        {
            info.AppendLine($"{"Size",w}{FormatSize(fi.Length)}");
            info.AppendLine($"{"Modified",w}{fi.LastWriteTime:yyyy-MM-dd HH:mm}");
        }

        var ddl = "-- Excel workbook — no SQL DDL.\n" +
                  $"-- Edit cells, add and delete rows in sheet '{sheetName}', then Save to write the file back.";
        return Task.FromResult(new TableStructure(ddl, info.ToString().TrimEnd(),
            "Excel files have no foreign-key relationships."));
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
