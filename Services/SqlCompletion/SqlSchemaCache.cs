using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using DataPortStudio.Models;
using Oracle.ManagedDataAccess.Client;

namespace DataPortStudio.Services.SqlCompletion;

/// <summary>
/// Holds table/column metadata for a connection+database.
/// For multi-schema engines (SQL Server), loads ALL user schemas so that
/// "dbo.Table" and "hr.Employee" completions work across schemas.
/// </summary>
public class SqlSchemaCache
{
    /// <summary>Short (unqualified) table names from the active schema.</summary>
    public List<string> Tables { get; } = [];

    /// <summary>column list keyed by both "TableName" and "schema.TableName".</summary>
    public Dictionary<string, List<string>> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All schema names found in the database (SQL Server / Oracle only).</summary>
    public List<string> Schemas { get; } = [];

    /// <summary>Tables grouped by schema name.</summary>
    public Dictionary<string, List<string>> SchemaToTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }

    public async Task LoadAsync(ConnectionProfile connection, string? database, string? activeSchema)
    {
        activeSchema ??= "dbo";
        try
        {
            if (connection.Engine == DatabaseEngine.Sqlite)
            {
                await LoadSqliteAsync(connection.BuildConnectionString());
                return;
            }

            await using var conn = CreateConnection(connection, database);
            await conn.OpenAsync();

            if (connection.Engine == DatabaseEngine.SqlServer)
            {
                await LoadSqlServerAllSchemasAsync(conn, activeSchema);
                return;
            }

            // Other engines: single-schema bulk load (MySQL, Firebird, Oracle)
            var (sql, p1, p2) = BulkQuery(connection.Engine, database, activeSchema);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (p1 != null) cmd.Parameters.Add(p1);
            if (p2 != null) cmd.Parameters.Add(p2);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var table  = reader.GetString(0).Trim();
                var column = reader.GetString(1).Trim();
                if (!Columns.ContainsKey(table)) { Tables.Add(table); Columns[table] = []; }
                Columns[table].Add(column);
            }
        }
        catch (Exception ex) { LoadError = ex.Message; }
        finally { IsLoaded = true; }
    }

    // ── SQL Server: loads ALL user schemas in one pass ────────────────────────

    private async Task LoadSqlServerAllSchemasAsync(DbConnection conn, string activeSchema)
    {
        const string sql = """
            SELECT s.name, t.name, c.name
            FROM sys.schemas  s
            JOIN sys.tables   t ON t.schema_id = s.schema_id
            JOIN sys.columns  c ON c.object_id  = t.object_id
            WHERE s.principal_id IS NOT NULL
              AND s.name NOT IN (
                'sys','INFORMATION_SCHEMA','guest',
                'db_owner','db_accessadmin','db_securityadmin',
                'db_backupoperator','db_datareader','db_datawriter',
                'db_ddladmin','db_denydatareader','db_denydatawriter')
            ORDER BY s.name, t.name, c.column_id
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();

        while (await r.ReadAsync())
        {
            var schema = r.GetString(0);
            var table  = r.GetString(1);
            var column = r.GetString(2);

            // Track schema names
            if (!SchemaToTables.ContainsKey(schema))
            {
                Schemas.Add(schema);
                SchemaToTables[schema] = [];
            }

            // Qualified key always
            var qKey = $"{schema}.{table}";
            if (!Columns.ContainsKey(qKey))
            {
                SchemaToTables[schema].Add(table);
                Columns[qKey] = [];
                // Active schema tables also available unqualified
                if (schema.Equals(activeSchema, StringComparison.OrdinalIgnoreCase))
                {
                    Tables.Add(table);
                    Columns[table] = [];
                }
            }
            Columns[qKey].Add(column);
            if (schema.Equals(activeSchema, StringComparison.OrdinalIgnoreCase))
                Columns[table].Add(column);
        }
    }

    // ── SQLite ────────────────────────────────────────────────────────────────

    private async Task LoadSqliteAsync(string cs)
    {
        var tables = await SqliteService.GetTablesAsync(cs);
        Tables.AddRange(tables);
        foreach (var t in tables)
            Columns[t] = await SqliteService.GetColumnNamesAsync(cs, t);
    }

    // ── Other engines (single-schema) ─────────────────────────────────────────

    private static (string Sql, DbParameter? P1, DbParameter? P2)
        BulkQuery(DatabaseEngine engine, string? database, string schema) => engine switch
    {
        DatabaseEngine.MySql or DatabaseEngine.MariaDb => (
            "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME, ORDINAL_POSITION",
            new MySqlParameter("@db", database ?? ""), null),

        DatabaseEngine.Firebird => (
            "SELECT TRIM(rf.RDB$RELATION_NAME), TRIM(rf.RDB$FIELD_NAME) " +
            "FROM RDB$RELATION_FIELDS rf " +
            "JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = rf.RDB$RELATION_NAME " +
            "WHERE (r.RDB$SYSTEM_FLAG = 0 OR r.RDB$SYSTEM_FLAG IS NULL) " +
            "  AND r.RDB$VIEW_BLR IS NULL " +
            "ORDER BY rf.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION",
            null, null),

        DatabaseEngine.Oracle => (
            "SELECT table_name, column_name FROM user_tab_columns " +
            "ORDER BY table_name, column_id",
            null, null),

        DatabaseEngine.PostgreSql => (
            "SELECT table_name, column_name FROM information_schema.columns " +
            "WHERE table_schema = @s ORDER BY table_name, ordinal_position",
            new NpgsqlParameter("@s", schema), null),

        _ => ("SELECT '' WHERE 1=0", null, null)
    };

    private static DbConnection CreateConnection(ConnectionProfile c, string? database)
    {
        var cs = c.BuildConnectionString();
        return c.Engine switch
        {
            DatabaseEngine.Sqlite   => new SqliteConnection(cs),
            DatabaseEngine.Firebird => new FbConnection(cs),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(database) ? cs : MySqlService.WithDatabase(cs, database)),
            DatabaseEngine.Oracle => new OracleConnection(cs),
            DatabaseEngine.PostgreSql =>
                new NpgsqlConnection(string.IsNullOrEmpty(database) ? cs : PostgresService.WithDatabase(cs, database)),
            _ => new SqlConnection(string.IsNullOrEmpty(database) ? cs : SqlServerService.WithDatabase(cs, database)),
        };
    }
}
