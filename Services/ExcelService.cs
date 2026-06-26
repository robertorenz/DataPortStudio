using ClosedXML.Excel;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using System.Data;
using System.IO;
using System.Text;

namespace DataPortStudio.Services;

/// <summary>
/// An "Excel connection" is a folder: each .xls/.xlsx file's worksheets appear as tables.
/// Sheet name is stored in Node.Schema; file name (e.g. "Sales.xlsx") in Node.Database.
/// Read-only — use Copy to move data into a SQL database.
/// </summary>
public record ExcelSheet(string DisplayName, string FileName, string SheetName);

public static class ExcelService
{
    private static bool IsXlsx(string ext) =>
        ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);

    private static bool IsXls(string ext) =>
        ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcelFile(string path)
    {
        var ext = Path.GetExtension(path);
        return IsXlsx(ext) || IsXls(ext);
    }

    /// <summary>Lists all sheets across every Excel file in the folder. Sorted by file name, then sheet order.</summary>
    public static List<ExcelSheet> ListSheets(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var result = new List<ExcelSheet>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsExcelFile)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            var ext = Path.GetExtension(file);
            try
            {
                List<string> sheets;
                if (IsXlsx(ext))
                {
                    using var wb = new XLWorkbook(file);
                    sheets = wb.Worksheets.Select(ws => ws.Name).ToList();
                }
                else
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var wb = new HSSFWorkbook(fs);
                    sheets = Enumerable.Range(0, wb.NumberOfSheets).Select(i => wb.GetSheetName(i)).ToList();
                }
                foreach (var sheet in sheets)
                    result.Add(new ExcelSheet($"{fileName} — {sheet}", fileName, sheet));
            }
            catch { /* skip corrupt or locked files */ }
        }
        return result;
    }

    /// <summary>Confirms the folder exists and contains at least one Excel file with at least one sheet.</summary>
    public static void TestConnection(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Choose a folder that contains Excel files (.xls or .xlsx).");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        if (ListSheets(folder).Count == 0)
            throw new FileNotFoundException($"No Excel files (.xls or .xlsx) were found in {folder}.");
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

        var ddl = $"-- Excel workbook — no SQL DDL.\n-- Data in sheet '{sheetName}' is read-only.";
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
