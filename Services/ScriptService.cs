using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DataPortStudio.Services;

/// <summary>Generates SQL scripts (e.g. INSERT statements) from table data.</summary>
public static class ScriptService
{
    private static string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    public static async Task<string> GenerateInsertsAsync(
        string connectionString, string database, string schema, string table, int limit)
    {
        var cs = SqlServerService.WithDatabase(connectionString, database);
        var fq = $"{Quote(schema)}.{Quote(table)}";

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();

        // Identity column (to wrap with SET IDENTITY_INSERT).
        bool hasIdentity;
        await using (var idCmd = new SqlCommand("SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(@fq)", conn))
        {
            idCmd.Parameters.AddWithValue("@fq", $"[{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]");
            hasIdentity = (int)(await idCmd.ExecuteScalarAsync() ?? 0) > 0;
        }

        var data = new DataTable();
        await using (var cmd = new SqlCommand($"SELECT TOP ({limit}) * FROM {fq}", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
            data.Load(reader);

        var cols = data.Columns.Cast<DataColumn>().ToList();
        var colList = string.Join(", ", cols.Select(c => Quote(c.ColumnName)));

        var sb = new StringBuilder();
        if (hasIdentity) sb.AppendLine($"SET IDENTITY_INSERT {fq} ON;");
        foreach (DataRow row in data.Rows)
        {
            var values = string.Join(", ", cols.Select(c => Literal(row[c])));
            sb.AppendLine($"INSERT INTO {fq} ({colList}) VALUES ({values});");
        }
        if (hasIdentity) sb.AppendLine($"SET IDENTITY_INSERT {fq} OFF;");

        if (data.Rows.Count >= limit)
            sb.AppendLine($"-- Note: limited to {limit} row(s).");

        return sb.ToString();
    }

    private static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => "N'" + s.Replace("'", "''") + "'",
        bool b => b ? "1" : "0",
        DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'",
        Guid g => "'" + g + "'",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "'" + value + "'"
    };
}
