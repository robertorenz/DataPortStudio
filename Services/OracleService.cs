using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace DataPortStudio.Services;

/// <summary>
/// Reads Oracle metadata and data (read-only). Browses the connected user's own schema via the
/// USER_* data-dictionary views. Identifiers are double-quoted and case-sensitive (Oracle stores
/// them upper-case by default); row limits use 12c+ <c>FETCH FIRST n ROWS ONLY</c>.
/// </summary>
public static class OracleService
{
    /// <summary>Double-quote an identifier.</summary>
    public static string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    public static async Task TestConnectionAsync(string connectionString)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync();
    }

    public static Task<List<string>> GetTablesAsync(string cs) =>
        ListAsync(cs, "SELECT table_name FROM user_tables ORDER BY table_name");

    public static Task<List<string>> GetViewsAsync(string cs) =>
        ListAsync(cs, "SELECT view_name FROM user_views ORDER BY view_name");

    public static Task<List<string>> GetFunctionsAsync(string cs) =>
        ListAsync(cs, "SELECT object_name FROM user_objects WHERE object_type = 'FUNCTION' ORDER BY object_name");

    public static Task<List<string>> GetProceduresAsync(string cs) =>
        ListAsync(cs, "SELECT object_name FROM user_objects WHERE object_type = 'PROCEDURE' ORDER BY object_name");

    private static async Task<List<string>> ListAsync(string cs, string sql)
    {
        var result = new List<string>();
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public record OracleColumn(string Name, string TypeName, bool Nullable, bool IsPrimaryKey, bool IsLob, string? Default);

    public static async Task<List<OracleColumn>> GetColumnsAsync(string cs, string table)
    {
        var pk = new HashSet<string>(await GetPrimaryKeyAsync(cs, table), StringComparer.OrdinalIgnoreCase);
        var result = new List<OracleColumn>();
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = (OracleCommand)conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = @"
            SELECT column_name, data_type, data_length, data_precision, data_scale, nullable, data_default
            FROM user_tab_columns WHERE table_name = :t ORDER BY column_id";
        cmd.Parameters.Add(new OracleParameter("t", table));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var dataType = r.GetString(1);
            var length = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
            var precision = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3));
            var scale = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r.GetValue(4));
            var nullable = r.GetString(5).Equals("Y", StringComparison.OrdinalIgnoreCase);
            var def = r.IsDBNull(6) ? null : r.GetValue(6)?.ToString()?.Trim();
            result.Add(new OracleColumn(name, FormatType(dataType, length, precision, scale), nullable,
                pk.Contains(name), IsLob(dataType), def));
        }
        return result;
    }

    private static bool IsLob(string dataType) =>
        dataType is "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW";

    private static string FormatType(string type, int length, int? precision, int? scale) => type switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" => $"{type}({length})",
        "NUMBER" when precision.HasValue && scale is > 0 => $"NUMBER({precision},{scale})",
        "NUMBER" when precision.HasValue => $"NUMBER({precision})",
        _ => type
    };

    public static async Task<List<string>> GetPrimaryKeyAsync(string cs, string table)
    {
        var pk = new List<string>();
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = (OracleCommand)conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = @"
            SELECT cc.column_name
            FROM user_constraints c
            JOIN user_cons_columns cc ON cc.constraint_name = c.constraint_name
            WHERE c.constraint_type = 'P' AND c.table_name = :t
            ORDER BY cc.position";
        cmd.Parameters.Add(new OracleParameter("t", table));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) pk.Add(r.GetString(0));
        return pk;
    }

    public static async Task<List<(string Name, bool Unique, List<string> Columns)>> GetIndexesAsync(string cs, string table)
    {
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = (OracleCommand)conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = @"
            SELECT i.index_name, i.uniqueness, ic.column_name
            FROM user_indexes i
            JOIN user_ind_columns ic ON ic.index_name = i.index_name
            WHERE i.table_name = :t
            ORDER BY i.index_name, ic.column_position";
        cmd.Parameters.Add(new OracleParameter("t", table));
        var map = new Dictionary<string, (bool Unique, List<string> Cols)>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = (r.GetString(1) == "UNIQUE", new()); map[n] = d; order.Add(n); }
            d.Cols.Add(r.GetString(2));
        }
        return order.Select(n => (n, map[n].Unique, map[n].Cols)).ToList();
    }

    public static async Task<long> GetRowCountAsync(string cs, string table)
    {
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Loads up to <paramref name="rowLimit"/> rows of a table/view into a DataTable using an already
    /// open connection. Reads each cell defensively: Oracle DATE/TIMESTAMP values outside .NET's
    /// DateTime range (BC years, corrupt zero-dates) — and any other unconvertible value — become NULL
    /// instead of throwing and aborting the whole table.
    /// </summary>
    public static async Task<DataTable> ReadTableAsync(OracleConnection conn, string table, int rowLimit)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Quote(table)} FETCH FIRST {rowLimit} ROWS ONLY";
        await using var reader = await cmd.ExecuteReaderAsync();

        var data = new DataTable(table);
        for (var i = 0; i < reader.FieldCount; i++)
            data.Columns.Add(reader.GetName(i), reader.GetFieldType(i) ?? typeof(object));

        while (await reader.ReadAsync())
        {
            var row = data.NewRow();
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = SafeGet(reader, i);
            data.Rows.Add(row);
        }
        return data;
    }

    private static object SafeGet(System.Data.Common.DbDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return DBNull.Value;
        try { return reader.GetValue(i) ?? DBNull.Value; }
        catch { return DBNull.Value; } // unrepresentable DATE/TIMESTAMP, oversized NUMBER, etc.
    }

    public static async Task ExecuteAsync(string cs, string sql)
    {
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
