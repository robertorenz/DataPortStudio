using System.Data;
using Microsoft.Data.SqlClient;

namespace DataPortStudio.Services;

/// <summary>Reads SQL Server metadata: databases, schemas, tables.</summary>
public static class SqlServerService
{
    /// <summary>Returns a copy of the connection string pointed at a specific database.</summary>
    public static string WithDatabase(string connectionString, string database)
        => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    public static async Task TestConnectionAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
    }

    public static async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Schemas in the given database that own any user object.</summary>
    public static async Task<List<string>> GetSchemasAsync(string connectionString, string database)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT DISTINCT s.name
              FROM sys.schemas s
              JOIN sys.objects o ON o.schema_id = s.schema_id
              WHERE o.type IN ('U','V','P','FN','IF','TF','FS','FT')
              ORDER BY s.name", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    public static Task<List<string>> GetTablesAsync(string connectionString, string database, string schema) =>
        GetObjectsAsync(connectionString, database, schema, "sys.tables");

    public static Task<List<string>> GetViewsAsync(string connectionString, string database, string schema) =>
        GetObjectsAsync(connectionString, database, schema, "sys.views");

    public static Task<List<string>> GetProceduresAsync(string connectionString, string database, string schema) =>
        GetObjectsAsync(connectionString, database, schema, "sys.procedures");

    public static async Task<List<string>> GetFunctionsAsync(string connectionString, string database, string schema)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT o.name
              FROM sys.objects o
              JOIN sys.schemas s ON o.schema_id = s.schema_id
              WHERE s.name = @schema AND o.type IN ('FN','IF','TF','FS','FT')
              ORDER BY o.name", conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>All user tables in the database as (schema, table).</summary>
    public static async Task<List<(string Schema, string Table)>> GetAllTablesAsync(string connectionString, string database)
    {
        var result = new List<(string, string)>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT s.name, t.name
              FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
              ORDER BY s.name, t.name", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    public record ColumnDetail(string Name, string TypeName, int MaxLength, byte Precision, byte Scale,
        bool Nullable, bool Identity, string? Default, string? DefaultName, bool IsPrimaryKey);

    private static string Bracketed(string schema, string name) =>
        $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]";

    public static async Task<List<ColumnDetail>> GetColumnDetailsAsync(string connectionString, string database, string schema, string table)
    {
        var result = new List<ColumnDetail>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        const string sql = @"
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   dc.definition, dc.name,
                   CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END
            FROM sys.columns c
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@fq)
            ORDER BY c.column_id";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", Bracketed(schema, table));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(new ColumnDetail(r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4),
                r.GetBoolean(5), r.GetBoolean(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt32(9) == 1));
        return result;
    }

    /// <summary>Primary key constraint name and its columns (in order).</summary>
    public static async Task<(string? Name, List<string> Columns)> GetPrimaryKeyAsync(
        string connectionString, string database, string schema, string table)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        const string sql = @"
            SELECT i.name, c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@fq) AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", Bracketed(schema, table));
        string? name = null;
        var cols = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) { name = r.GetString(0); cols.Add(r.GetString(1)); }
        return (name, cols);
    }

    public record IndexDetail(string Name, bool Unique, List<string> Columns);

    /// <summary>Secondary indexes (not the primary key or unique constraints).</summary>
    public static async Task<List<IndexDetail>> GetIndexesAsync(
        string connectionString, string database, string schema, string table)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        const string sql = @"
            SELECT i.name, i.is_unique, c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@fq) AND i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
            ORDER BY i.index_id, ic.key_ordinal";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", Bracketed(schema, table));
        var map = new Dictionary<string, IndexDetail>();
        var order = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var n = r.GetString(0);
            if (!map.TryGetValue(n, out var d)) { d = new IndexDetail(n, r.GetBoolean(1), new()); map[n] = d; order.Add(n); }
            d.Columns.Add(r.GetString(2));
        }
        return order.Select(n => map[n]).ToList();
    }

    /// <summary>The CREATE definition of a programmable object (function/proc/view/trigger), or null.</summary>
    public static async Task<string?> GetObjectDefinitionAsync(string connectionString, string database, string schema, string name)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@fq))", conn);
        var fq = $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]";
        cmd.Parameters.AddWithValue("@fq", fq);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>Objects that reference the given object (dependents), as "schema.name (type)".</summary>
    public static async Task<List<string>> GetDependentsAsync(string connectionString, string database, string schema, string name)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        const string sql = @"
            SELECT DISTINCT OBJECT_SCHEMA_NAME(d.referencing_id), OBJECT_NAME(d.referencing_id), o.type_desc
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            WHERE d.referenced_id = OBJECT_ID(@fq)";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]");
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var s = r.IsDBNull(0) ? "" : r.GetString(0);
            var n = r.IsDBNull(1) ? "?" : r.GetString(1);
            var t = r.IsDBNull(2) ? "" : r.GetString(2).Replace("_", " ").ToLowerInvariant();
            result.Add($"{(s.Length > 0 ? s + "." : "")}{n} ({t})");
        }
        return result;
    }

    /// <summary>Runs a DDL/script batch (no result set). Returns rows affected.</summary>
    public static async Task<int> ExecuteAsync(string connectionString, string database, string sql)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> GetColumnNamesAsync(string connectionString, string database, string schema, string table)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT c.name
              FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id
              JOIN sys.schemas s ON o.schema_id = s.schema_id
              WHERE s.name = @s AND o.name = @t
              ORDER BY c.column_id", conn);
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    /// <summary>All foreign keys in the database as (parentSchema,parentTable,parentCol,refSchema,refTable,refCol).</summary>
    public static async Task<List<(string PS, string PT, string PC, string RS, string RT, string RC)>>
        GetAllForeignKeysAsync(string connectionString, string database)
    {
        var result = new List<(string, string, string, string, string, string)>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id), pc.name,
                     OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id), rc.name
              FROM sys.foreign_keys fk
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        return result;
    }

    private static async Task<List<string>> GetObjectsAsync(
        string connectionString, string database, string schema, string sysView)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $@"SELECT o.name
               FROM {sysView} o
               JOIN sys.schemas s ON o.schema_id = s.schema_id
               WHERE s.name = @schema
               ORDER BY o.name", conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}
