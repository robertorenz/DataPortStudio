using Npgsql;

namespace DataPortStudio.Services;

/// <summary>Reads PostgreSQL metadata (information_schema + pg_catalog).</summary>
public static class PostgresService
{
    public static string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    public static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    public static string WithoutDatabase(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;

    public static async Task TestConnectionAsync(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(csb.Database)) csb.Database = "postgres";
        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();
    }

    public static async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var result = new List<string>();
        await using var conn = new NpgsqlConnection(WithoutDatabase(connectionString));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY datname";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public static async Task<List<string>> GetSchemasAsync(string connectionString, string database)
    {
        var result = new List<string>();
        await using var conn = new NpgsqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT schema_name
            FROM information_schema.schemata
            WHERE schema_name NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
              AND schema_name NOT LIKE 'pg_temp_%'
              AND schema_name NOT LIKE 'pg_toast_temp_%'
            ORDER BY schema_name";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public static Task<List<string>> GetTablesAsync(string cs, string db, string schema) =>
        ListAsync(cs, db, "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name", schema);

    public static Task<List<string>> GetViewsAsync(string cs, string db, string schema) =>
        ListAsync(cs, db, "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'VIEW' ORDER BY table_name", schema);

    public static Task<List<string>> GetFunctionsAsync(string cs, string db, string schema) =>
        ListAsync(cs, db, "SELECT DISTINCT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'FUNCTION' ORDER BY routine_name", schema);

    public static Task<List<string>> GetProceduresAsync(string cs, string db, string schema) =>
        ListAsync(cs, db, "SELECT DISTINCT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'PROCEDURE' ORDER BY routine_name", schema);

    private static async Task<List<string>> ListAsync(string cs, string db, string sql, string schema)
    {
        var result = new List<string>();
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@s", schema);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public record PgColumn(string Name, string TypeName, bool Nullable, bool IsPrimaryKey, bool IsLargeObject, string? Default);

    public static async Task<List<PgColumn>> GetColumnsAsync(string cs, string db, string schema, string table)
    {
        var result = new List<PgColumn>();
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.column_default,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                c.udt_name,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END AS is_pk
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON kcu.constraint_name = tc.constraint_name
                    AND kcu.constraint_schema = tc.constraint_schema
                WHERE tc.table_schema = @s AND tc.table_name = @t
                    AND tc.constraint_type = 'PRIMARY KEY'
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @s AND c.table_name = @t
            ORDER BY c.ordinal_position";
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var dataType = r.GetString(1);
            var charMaxLen = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);
            var numPrec = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);
            var numScale = r.IsDBNull(6) ? (int?)null : r.GetInt32(6);
            var udtName = r.IsDBNull(7) ? "" : r.GetString(7);
            var typeName = BuildTypeName(dataType, charMaxLen, numPrec, numScale, udtName);
            var isLob = dataType.Equals("bytea", StringComparison.OrdinalIgnoreCase);
            result.Add(new PgColumn(
                r.GetString(0),
                typeName,
                r.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                r.GetBoolean(8),
                isLob,
                r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return result;
    }

    private static string BuildTypeName(string dataType, int? charMaxLen, int? numPrec, int? numScale, string udtName)
    {
        return dataType.ToLowerInvariant() switch
        {
            "character varying" => charMaxLen.HasValue ? $"varchar({charMaxLen})" : "varchar",
            "character" => charMaxLen.HasValue ? $"char({charMaxLen})" : "char",
            "numeric" when numPrec.HasValue && numScale.HasValue => $"numeric({numPrec},{numScale})",
            "numeric" when numPrec.HasValue => $"numeric({numPrec})",
            "bit" when charMaxLen.HasValue => $"bit({charMaxLen})",
            "bit varying" when charMaxLen.HasValue => $"varbit({charMaxLen})",
            "array" => udtName.TrimStart('_') + "[]",
            "user-defined" => udtName,
            _ => dataType
        };
    }

    public static async Task<List<string>> GetColumnNamesAsync(string cs, string db, string schema, string table)
    {
        var result = new List<string>();
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public static async Task<List<string>> GetPrimaryKeyAsync(string cs, string db, string schema, string table)
    {
        var pk = new List<string>();
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON kcu.constraint_name = tc.constraint_name
                AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.table_schema = @s AND tc.table_name = @t
                AND tc.constraint_type = 'PRIMARY KEY'
            ORDER BY kcu.ordinal_position";
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) pk.Add(r.GetString(0));
        return pk;
    }

    public static async Task<List<(string Name, bool Unique, List<string> Columns)>> GetIndexesAsync(string cs, string db, string schema, string table)
    {
        var result = new List<(string, bool, List<string>)>();
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                i.relname AS index_name,
                ix.indisunique,
                (SELECT string_agg(a.attname, ',' ORDER BY array_position(ix.indkey, a.attnum))
                 FROM pg_attribute a
                 WHERE a.attrelid = t.oid AND a.attnum = ANY(ix.indkey) AND a.attnum > 0
                ) AS columns
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = @s AND t.relname = @t AND NOT ix.indisprimary
            ORDER BY i.relname";
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var cols = r.IsDBNull(2) ? new List<string>() : r.GetString(2).Split(',').ToList();
            result.Add((r.GetString(0), r.GetBoolean(1), cols));
        }
        return result;
    }

    public static async Task<List<(string Name, List<string> Cols, string RefTable, List<string> RefCols)>>
        GetForeignKeysAsync(string cs, string db, string schema, string table)
    {
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tc.constraint_name, kcu.column_name, ccu.table_name, ccu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
            JOIN information_schema.constraint_column_usage ccu
                ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
                AND tc.table_schema = @s AND tc.table_name = @t
            ORDER BY tc.constraint_name, kcu.ordinal_position";
        cmd.Parameters.AddWithValue("@s", schema);
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

    public static async Task<long> GetRowCountAsync(string cs, string db, string schema, string table)
    {
        await using var conn = new NpgsqlConnection(WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(schema)}.{Quote(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task<System.Data.DataTable> ReadTableAsync(NpgsqlConnection connection, string schema, string table, int rowLimit)
    {
        var fq = $"{Quote(schema)}.{Quote(table)}";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {fq} LIMIT {rowLimit}";
        await using var reader = await cmd.ExecuteReaderAsync();
        var dt = new System.Data.DataTable(table);
        // Load defensively: skip schema constraints that may fail on real data.
        for (var i = 0; i < reader.FieldCount; i++)
            dt.Columns.Add(reader.GetName(i), reader.GetFieldType(i) ?? typeof(object));
        while (await reader.ReadAsync())
        {
            var row = dt.NewRow();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i)) { row[i] = System.DBNull.Value; continue; }
                try { row[i] = reader.GetValue(i) ?? System.DBNull.Value; }
                catch { row[i] = System.DBNull.Value; }
            }
            dt.Rows.Add(row);
        }
        return dt;
    }

    public static async Task ExecuteAsync(string cs, string db, string sql)
    {
        await using var conn = new NpgsqlConnection(string.IsNullOrEmpty(db) ? cs : WithDatabase(cs, db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
