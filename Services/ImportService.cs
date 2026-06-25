using System.IO;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;

namespace DataPortStudio.Services;

/// <summary>Reads CSV / Excel files and imports rows into a table.</summary>
public static class ImportService
{
    public record ParsedFile(List<string> Headers, List<string[]> Rows);

    public static ParsedFile Read(string path, bool firstRowHeader)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var all = ext == ".xlsx" ? ReadXlsx(path) : ReadCsv(path);

        if (firstRowHeader && all.Count > 0)
            return new ParsedFile(all[0].ToList(), all.Skip(1).ToList());

        var width = all.Count > 0 ? all.Max(r => r.Length) : 0;
        var headers = Enumerable.Range(1, width).Select(i => "Column" + i).ToList();
        return new ParsedFile(headers, all);
    }

    private static List<string[]> ReadXlsx(string path)
    {
        var rows = new List<string[]>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault();
        var range = ws?.RangeUsed();
        if (range is null) return rows;
        var cols = range.ColumnCount();
        foreach (var r in range.Rows())
            rows.Add(Enumerable.Range(1, cols).Select(i => r.Cell(i).GetString()).ToArray());
        return rows;
    }

    private static List<string[]> ReadCsv(string path)
    {
        var text = File.ReadAllText(path);
        var rows = new List<string[]>();
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
                    case ',': row.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break;
                    case '\n': row.Add(sb.ToString()); sb.Clear(); rows.Add(row.ToArray()); row.Clear(); break;
                    default: sb.Append(ch); break;
                }
            }
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    /// <summary>Inserts rows. mapping: table column -> source column index. Returns (inserted, error).</summary>
    public static async Task<(int Inserted, string? Error)> ImportAsync(
        string connectionString, string database, string schema, string table,
        IReadOnlyList<(string Column, int SourceIndex)> mapping, List<string[]> rows)
    {
        if (mapping.Count == 0) return (0, "No columns mapped.");

        var cs = SqlServerService.WithDatabase(connectionString, database);
        var fq = $"[{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]";
        var colList = string.Join(", ", mapping.Select(m => $"[{m.Column.Replace("]", "]]")}]"));
        var paramList = string.Join(", ", mapping.Select((_, i) => "@p" + i));
        var sql = $"INSERT INTO {fq} ({colList}) VALUES ({paramList})";

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        var inserted = 0;
        try
        {
            foreach (var r in rows)
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                for (var i = 0; i < mapping.Count; i++)
                {
                    var idx = mapping[i].SourceIndex;
                    var raw = idx >= 0 && idx < r.Length ? r[idx] : null;
                    cmd.Parameters.AddWithValue("@p" + i,
                        string.IsNullOrEmpty(raw) ? DBNull.Value : raw);
                }
                inserted += await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return (inserted, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return (0, $"Row {inserted + 1}: {ex.Message}");
        }
    }
}
