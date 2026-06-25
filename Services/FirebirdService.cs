using FirebirdSql.Data.FirebirdClient;

namespace DataPortStudio.Services;

/// <summary>Reads Firebird metadata via the RDB$ system tables.</summary>
public static class FirebirdService
{
    /// <summary>Double-quote an identifier (Firebird is case-sensitive for quoted names).</summary>
    public static string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    public static async Task TestConnectionAsync(string connectionString)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
    }

    public static Task<List<string>> GetTablesAsync(string cs) => GetRelationsAsync(cs, views: false);
    public static Task<List<string>> GetViewsAsync(string cs) => GetRelationsAsync(cs, views: true);

    private static async Task<List<string>> GetRelationsAsync(string connectionString, bool views)
    {
        var result = new List<string>();
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS " +
            "WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 AND RDB$VIEW_BLR IS " + (views ? "NOT NULL" : "NULL") +
            " ORDER BY RDB$RELATION_NAME";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public static async Task<List<string>> GetColumnNamesAsync(string connectionString, string table)
    {
        var result = new List<string>();
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS WHERE RDB$RELATION_NAME = @t " +
            "ORDER BY RDB$FIELD_POSITION";
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public record FbColumn(string Name, string TypeName, bool Nullable, bool IsPrimaryKey, bool IsBlob);

    public static async Task<List<FbColumn>> GetColumnsAsync(string connectionString, string table)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();

        var pk = await ReadPrimaryKeyAsync(conn, table);

        var result = new List<FbColumn>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TRIM(rf.RDB$FIELD_NAME), f.RDB$FIELD_TYPE, f.RDB$FIELD_SUB_TYPE,
                   f.RDB$FIELD_LENGTH, f.RDB$FIELD_PRECISION, f.RDB$FIELD_SCALE,
                   f.RDB$CHARACTER_LENGTH, COALESCE(rf.RDB$NULL_FLAG,0)
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            WHERE rf.RDB$RELATION_NAME = @t
            ORDER BY rf.RDB$FIELD_POSITION";
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var type = r.GetInt32(1);
            int? sub = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2));
            int? len = r.IsDBNull(3) ? null : Convert.ToInt32(r.GetValue(3));
            int? prec = r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4));
            int? scale = r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5));
            int? charLen = r.IsDBNull(6) ? null : Convert.ToInt32(r.GetValue(6));
            var notNull = Convert.ToInt32(r.GetValue(7)) == 1;
            result.Add(new FbColumn(name, MapType(type, sub, len, prec, scale, charLen),
                !notNull, pk.Contains(name, StringComparer.OrdinalIgnoreCase), type == 261));
        }
        return result;
    }

    public static async Task<List<string>> GetPrimaryKeyAsync(string connectionString, string table)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        return await ReadPrimaryKeyAsync(conn, table);
    }

    private static async Task<List<string>> ReadPrimaryKeyAsync(FbConnection conn, string table)
    {
        var pk = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TRIM(s.RDB$FIELD_NAME)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            WHERE rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY' AND rc.RDB$RELATION_NAME = @t
            ORDER BY s.RDB$FIELD_POSITION";
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) pk.Add(r.GetString(0));
        return pk;
    }

    /// <summary>Secondary indexes (not backing a PK/UNIQUE/FK constraint).</summary>
    public static async Task<List<(string Name, bool Unique, List<string> Columns)>> GetIndexesAsync(
        string connectionString, string table)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TRIM(i.RDB$INDEX_NAME), COALESCE(i.RDB$UNIQUE_FLAG,0), TRIM(s.RDB$FIELD_NAME)
            FROM RDB$INDICES i
            JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = i.RDB$INDEX_NAME
            WHERE i.RDB$RELATION_NAME = @t AND COALESCE(i.RDB$SYSTEM_FLAG,0)=0
              AND NOT EXISTS (SELECT 1 FROM RDB$RELATION_CONSTRAINTS rc WHERE rc.RDB$INDEX_NAME = i.RDB$INDEX_NAME)
            ORDER BY i.RDB$INDEX_NAME, s.RDB$FIELD_POSITION";
        cmd.Parameters.AddWithValue("@t", table);

        var map = new Dictionary<string, (bool Unique, List<string> Cols)>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = (Convert.ToInt32(r.GetValue(1)) == 1, new List<string>()); map[n] = d; order.Add(n); }
            d.Cols.Add(r.GetString(2));
        }
        return order.Select(n => (n, map[n].Unique, map[n].Cols)).ToList();
    }

    /// <summary>Outgoing foreign keys as (constraint, localCols, refTable, refCols).</summary>
    public static async Task<List<(string Name, List<string> Cols, string RefTable, List<string> RefCols)>>
        GetForeignKeysAsync(string connectionString, string table)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TRIM(rc.RDB$CONSTRAINT_NAME), TRIM(s.RDB$FIELD_NAME),
                   TRIM(rc2.RDB$RELATION_NAME), TRIM(s2.RDB$FIELD_NAME), s.RDB$FIELD_POSITION
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$REF_CONSTRAINTS ref ON ref.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME
            JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = ref.RDB$CONST_NAME_UQ
            JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            JOIN RDB$INDEX_SEGMENTS s2 ON s2.RDB$INDEX_NAME = rc2.RDB$INDEX_NAME AND s2.RDB$FIELD_POSITION = s.RDB$FIELD_POSITION
            WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND rc.RDB$RELATION_NAME = @t
            ORDER BY rc.RDB$CONSTRAINT_NAME, s.RDB$FIELD_POSITION";
        cmd.Parameters.AddWithValue("@t", table);

        var map = new Dictionary<string, (List<string> Cols, string RefTable, List<string> RefCols)>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = (new List<string>(), r.GetString(2), new List<string>()); map[n] = d; order.Add(n); }
            d.Cols.Add(r.GetString(1));
            d.RefCols.Add(r.GetString(3));
        }
        return order.Select(n => (n, map[n].Cols, map[n].RefTable, map[n].RefCols)).ToList();
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<long> GetRowCountAsync(string connectionString, string table)
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ---- type mapping ----------------------------------------------------

    private static string MapType(int type, int? sub, int? len, int? prec, int? scale, int? charLen)
    {
        var s = scale ?? 0;
        switch (type)
        {
            case 7: return Numeric(sub, prec, s, "SMALLINT");
            case 8: return Numeric(sub, prec, s, "INTEGER");
            case 16: return Numeric(sub, prec, s, "BIGINT");
            case 10: return "FLOAT";
            case 27: return "DOUBLE PRECISION";
            case 12: return "DATE";
            case 13: return "TIME";
            case 35: return "TIMESTAMP";
            case 23: return "BOOLEAN";
            case 14: return $"CHAR({charLen ?? len ?? 0})";
            case 37: return $"VARCHAR({charLen ?? len ?? 0})";
            case 261: return sub == 1 ? "BLOB SUB_TYPE TEXT" : "BLOB";
            default: return "UNKNOWN";
        }
    }

    private static string Numeric(int? sub, int? prec, int scale, string baseName)
    {
        if (sub is 1 or 2 || scale < 0)
        {
            var kw = sub == 2 ? "DECIMAL" : "NUMERIC";
            return $"{kw}({prec ?? 18},{-scale})";
        }
        return baseName;
    }
}
