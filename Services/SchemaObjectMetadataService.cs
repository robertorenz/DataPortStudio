using System.Data.Common;
using DataPortStudio.Models;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace DataPortStudio.Services;

public sealed record ProgrammableObjectInfo(SchemaObjectType Type, string Name, string? Definition);

/// <summary>Reads views and routines in batches so Schema Diff can compare their source text.</summary>
public static class SchemaObjectMetadataService
{
    public static async Task<List<ProgrammableObjectInfo>> LoadAsync(
        SchemaEndpoint endpoint, IReadOnlySet<SchemaObjectType> requested)
    {
        var result = new List<ProgrammableObjectInfo>();
        foreach (var type in requested.Where(t => t != SchemaObjectType.Table))
        {
            try
            {
                result.AddRange(await LoadTypeAsync(endpoint, type));
            }
            catch (Exception) when (!Supports(endpoint.Connection.Engine, type))
            {
                // An unavailable object category on an engine is an empty category, not a failed diff.
            }
        }
        return result;
    }

    private static bool Supports(DatabaseEngine engine, SchemaObjectType type) => engine switch
    {
        DatabaseEngine.Sqlite => type == SchemaObjectType.View,
        DatabaseEngine.Firebird => type is SchemaObjectType.View or SchemaObjectType.Function or SchemaObjectType.Procedure,
        _ => true
    };

    private static async Task<List<ProgrammableObjectInfo>> LoadTypeAsync(
        SchemaEndpoint endpoint, SchemaObjectType type)
    {
        var p = endpoint.Connection;
        var cs = p.BuildConnectionString();
        return p.Engine switch
        {
            DatabaseEngine.SqlServer => await LoadSqlServerAsync(cs, endpoint.Database, endpoint.Schema, type),
            DatabaseEngine.PostgreSql => await LoadPostgresAsync(cs, endpoint.Database, endpoint.Schema, type),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => await LoadMySqlAsync(cs, endpoint.Database, type),
            DatabaseEngine.Sqlite => await LoadSqliteAsync(cs, type),
            DatabaseEngine.Firebird => await LoadFirebirdAsync(cs, type),
            DatabaseEngine.Oracle => await LoadOracleAsync(cs, type),
            _ => []
        };
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadSqlServerAsync(
        string cs, string database, string schema, SchemaObjectType type)
    {
        var codes = type switch
        {
            SchemaObjectType.View => "'V'",
            SchemaObjectType.Function => "'FN','IF','TF','FS','FT'",
            SchemaObjectType.Procedure => "'P','PC'",
            _ => "''"
        };
        await using var conn = new SqlConnection(SqlServerService.WithDatabase(cs, database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT o.name, m.definition
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE s.name = @schema AND o.type IN ({codes})
            ORDER BY o.name
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        return await ReadAsync(cmd, type);
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadPostgresAsync(
        string cs, string database, string schema, SchemaObjectType type)
    {
        await using var conn = new NpgsqlConnection(PostgresService.WithDatabase(cs, database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (type == SchemaObjectType.View)
        {
            cmd.CommandText = """
                SELECT viewname, definition
                FROM pg_views WHERE schemaname = @schema ORDER BY viewname
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
                       pg_get_functiondef(p.oid)
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = @schema AND p.prokind::text = @kind
                ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)
                """;
            cmd.Parameters.AddWithValue("@kind", type == SchemaObjectType.Procedure ? "p" : "f");
        }
        cmd.Parameters.AddWithValue("@schema", schema);
        return await ReadAsync(cmd, type);
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadMySqlAsync(
        string cs, string database, SchemaObjectType type)
    {
        await using var conn = new MySqlConnection(MySqlService.WithDatabase(cs, database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (type == SchemaObjectType.View)
        {
            cmd.CommandText = """
                SELECT TABLE_NAME, VIEW_DEFINITION
                FROM information_schema.VIEWS
                WHERE TABLE_SCHEMA = @database ORDER BY TABLE_NAME
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT ROUTINE_NAME, ROUTINE_DEFINITION
                FROM information_schema.ROUTINES
                WHERE ROUTINE_SCHEMA = @database AND ROUTINE_TYPE = @kind
                ORDER BY ROUTINE_NAME
                """;
            cmd.Parameters.AddWithValue("@kind",
                type == SchemaObjectType.Procedure ? "PROCEDURE" : "FUNCTION");
        }
        cmd.Parameters.AddWithValue("@database", database);
        var objects = await ReadAsync(cmd, type);

        // INFORMATION_SCHEMA omits routine parameters and return types. SHOW CREATE provides a
        // transferable definition; remove endpoint-specific database qualification for comparison.
        for (var i = 0; i < objects.Count; i++)
        {
            await using var create = conn.CreateCommand();
            var keyword = type switch
            {
                SchemaObjectType.View => "VIEW",
                SchemaObjectType.Function => "FUNCTION",
                _ => "PROCEDURE"
            };
            create.CommandText = $"SHOW CREATE {keyword} {MySqlService.Quote(objects[i].Name)}";
            await using var reader = await create.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) continue;
            string? ddl = null;
            for (var column = 0; column < reader.FieldCount; column++)
            {
                if (!reader.GetName(column).StartsWith("Create ", StringComparison.OrdinalIgnoreCase) ||
                    reader.IsDBNull(column)) continue;
                ddl = reader.GetValue(column)?.ToString();
                break;
            }
            if (!string.IsNullOrWhiteSpace(ddl))
                objects[i] = objects[i] with { Definition = ddl };
        }
        return objects;
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadSqliteAsync(
        string cs, SchemaObjectType type)
    {
        if (type != SchemaObjectType.View) return [];
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'view' ORDER BY name";
        return await ReadAsync(cmd, type);
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadFirebirdAsync(
        string cs, SchemaObjectType type)
    {
        await using var conn = new FbConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = type switch
        {
            SchemaObjectType.View => """
                SELECT TRIM(RDB$RELATION_NAME), RDB$VIEW_SOURCE
                FROM RDB$RELATIONS
                WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 AND RDB$VIEW_BLR IS NOT NULL
                ORDER BY RDB$RELATION_NAME
                """,
            SchemaObjectType.Procedure => """
                SELECT TRIM(RDB$PROCEDURE_NAME), RDB$PROCEDURE_SOURCE
                FROM RDB$PROCEDURES
                WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 ORDER BY RDB$PROCEDURE_NAME
                """,
            SchemaObjectType.Function => """
                SELECT TRIM(RDB$FUNCTION_NAME), RDB$FUNCTION_SOURCE
                FROM RDB$FUNCTIONS
                WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 ORDER BY RDB$FUNCTION_NAME
                """,
            _ => "SELECT CAST(NULL AS VARCHAR(1)), CAST(NULL AS BLOB) FROM RDB$DATABASE WHERE 1=0"
        };
        return await ReadAsync(cmd, type);
    }

    private static async Task<List<ProgrammableObjectInfo>> LoadOracleAsync(
        string cs, SchemaObjectType type)
    {
        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        await using var cmd = (OracleCommand)conn.CreateCommand();
        if (type == SchemaObjectType.View)
        {
            cmd.CommandText = "SELECT view_name, text FROM user_views ORDER BY view_name";
            return await ReadAsync(cmd, type);
        }

        cmd.BindByName = true;
        cmd.CommandText = """
            SELECT name, text
            FROM user_source
            WHERE type = :kind
            ORDER BY name, line
            """;
        cmd.Parameters.Add(new OracleParameter("kind",
            type == SchemaObjectType.Procedure ? "PROCEDURE" : "FUNCTION"));

        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (!grouped.TryGetValue(name, out var lines)) grouped[name] = lines = [];
            if (!reader.IsDBNull(1)) lines.Add(reader.GetString(1));
        }
        return grouped.Select(x => new ProgrammableObjectInfo(type, x.Key, string.Concat(x.Value))).ToList();
    }

    private static async Task<List<ProgrammableObjectInfo>> ReadAsync(
        DbCommand command, SchemaObjectType type)
    {
        var result = new List<ProgrammableObjectInfo>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString()?.Trim() ?? "";
            if (name.Length == 0) continue;
            var definition = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
            result.Add(new ProgrammableObjectInfo(type, name, definition));
        }
        return result;
    }
}
