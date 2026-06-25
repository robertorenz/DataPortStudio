using MySqlConnector;

namespace DataPortStudio.Services;

/// <summary>Reads MySQL / MariaDB metadata (information_schema). Both engines use the same driver.</summary>
public static class MySqlService
{
    /// <summary>Backtick-quote an identifier.</summary>
    public static string Quote(string id) => "`" + id.Replace("`", "``") + "`";

    /// <summary>Connection string pointed at a specific database (schema).</summary>
    public static string WithDatabase(string connectionString, string database) =>
        new MySqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    /// <summary>Connection string with no default schema — for server-level work (test, list databases).</summary>
    public static string WithoutDatabase(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;

    public static async Task TestConnectionAsync(string connectionString)
    {
        // Test server reachability + credentials only; don't require a valid default database
        // (a bogus/empty "Default database" must not fail the test).
        await using var conn = new MySqlConnection(WithoutDatabase(connectionString));
        await conn.OpenAsync();
    }

    private static readonly string[] SystemDbs = { "information_schema", "mysql", "performance_schema", "sys" };

    public static async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var result = new List<string>();
        // List every schema on the server — never pin to the (optional) default database.
        await using var conn = new MySqlConnection(WithoutDatabase(connectionString));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!SystemDbs.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
        }
        return result;
    }

    public static Task<List<string>> GetTablesAsync(string cs, string db) =>
        ListAsync(cs, "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@d AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME", db);

    public static Task<List<string>> GetViewsAsync(string cs, string db) =>
        ListAsync(cs, "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@d AND TABLE_TYPE='VIEW' ORDER BY TABLE_NAME", db);

    public static Task<List<string>> GetFunctionsAsync(string cs, string db) =>
        ListAsync(cs, "SELECT ROUTINE_NAME FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@d AND ROUTINE_TYPE='FUNCTION' ORDER BY ROUTINE_NAME", db);

    public static Task<List<string>> GetProceduresAsync(string cs, string db) =>
        ListAsync(cs, "SELECT ROUTINE_NAME FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@d AND ROUTINE_TYPE='PROCEDURE' ORDER BY ROUTINE_NAME", db);

    private static async Task<List<string>> ListAsync(string cs, string sql, string db)
    {
        var result = new List<string>();
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@d", db);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public static async Task<List<string>> GetColumnNamesAsync(string cs, string db, string table)
    {
        var result = new List<string>();
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@d", db);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public record MyColumn(string Name, string TypeName, bool Nullable, bool IsPrimaryKey, bool IsBlob, string? Default, bool AutoIncrement);

    public static async Task<List<MyColumn>> GetColumnsAsync(string cs, string db, string table)
    {
        var result = new List<MyColumn>();
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA, DATA_TYPE
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t
            ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@d", db);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var dataType = r.GetString(6).ToLowerInvariant();
            result.Add(new MyColumn(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                r.GetString(3).Equals("PRI", StringComparison.OrdinalIgnoreCase),
                dataType.Contains("blob") || dataType is "text" or "longtext" or "mediumtext" or "json" or "geometry",
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5).Contains("auto_increment", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    public static async Task<List<string>> GetPrimaryKeyAsync(string cs, string db, string table)
    {
        var pk = new List<string>();
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT k.COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE k
            WHERE k.TABLE_SCHEMA=@d AND k.TABLE_NAME=@t AND k.CONSTRAINT_NAME='PRIMARY'
            ORDER BY k.ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@d", db);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) pk.Add(r.GetString(0));
        return pk;
    }

    public static async Task<List<(string Name, bool Unique, List<string> Columns)>> GetIndexesAsync(string cs, string db, string table)
    {
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t AND INDEX_NAME<>'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX";
        cmd.Parameters.AddWithValue("@d", db);
        cmd.Parameters.AddWithValue("@t", table);
        var map = new Dictionary<string, (bool Unique, List<string> Cols)>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = (r.GetInt32(1) == 0, new()); map[n] = d; order.Add(n); }
            d.Cols.Add(r.GetString(2));
        }
        return order.Select(n => (n, map[n].Unique, map[n].Cols)).ToList();
    }

    public static async Task<List<(string Name, List<string> Cols, string RefTable, List<string> RefCols)>>
        GetForeignKeysAsync(string cs, string db, string table)
    {
        await using var conn = new MySqlConnection(WithoutDatabase(cs));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@d", db);
        cmd.Parameters.AddWithValue("@t", table);
        var map = new Dictionary<string, (List<string> Cols, string RefTable, List<string> RefCols)>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = (new(), r.GetString(2), new()); map[n] = d; order.Add(n); }
            d.Cols.Add(r.GetString(1));
            d.RefCols.Add(r.GetString(3));
        }
        return order.Select(n => (n, map[n].Cols, map[n].RefTable, map[n].RefCols)).ToList();
    }

    public static async Task<string?> GetCreateTableAsync(string cs, string db, string table)
    {
        await using var conn = new MySqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE {Quote(table)}";
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() && r.FieldCount > 1 ? r.GetString(1) : null;
    }

    public static async Task<long> GetRowCountAsync(string cs, string db, string table)
    {
        await using var conn = new MySqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task ExecuteAsync(string cs, string db, string sql)
    {
        await using var conn = new MySqlConnection(string.IsNullOrEmpty(db) ? cs : WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
