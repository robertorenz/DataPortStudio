using System.Data;
using DataPortStudio.Models;
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

    /// <summary>
    /// User-facing schemas in the database, including empty schemas such as dbo in a newly
    /// created database. Fixed-role and system schemas are intentionally hidden.
    /// </summary>
    public static async Task<List<string>> GetSchemasAsync(string connectionString, string database)
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT s.name
              FROM sys.schemas s
              WHERE s.name NOT IN (
                  N'sys', N'INFORMATION_SCHEMA', N'guest',
                  N'db_owner', N'db_accessadmin', N'db_securityadmin', N'db_ddladmin',
                  N'db_backupoperator', N'db_datareader', N'db_datawriter',
                  N'db_denydatareader', N'db_denydatawriter'
              )
              ORDER BY CASE WHEN s.name = N'dbo' THEN 0 ELSE 1 END, s.name", conn);
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

    public record ForeignKeyDetail(
        string Name,
        string ParentSchema,
        string ParentTable,
        List<string> ParentColumns,
        string ReferencedSchema,
        string ReferencedTable,
        List<string> ReferencedColumns,
        string DeleteAction,
        string UpdateAction);

    /// <summary>Foreign keys declared by, or referencing, a table.</summary>
    public static async Task<List<ForeignKeyDetail>> GetForeignKeysAsync(
        string connectionString, string database, string schema, string table)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, database));
        await conn.OpenAsync();
        const string sql = @"
            SELECT fk.object_id, fk.name,
                   ps.name, pt.name, pc.name,
                   rs.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.columns pc ON pc.object_id = pt.object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@fq) OR fk.referenced_object_id = OBJECT_ID(@fq)
            ORDER BY fk.object_id, fkc.constraint_column_id";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fq", Bracketed(schema, table));
        var map = new Dictionary<int, ForeignKeyDetail>();
        var order = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            if (!map.TryGetValue(id, out var fk))
            {
                fk = new ForeignKeyDetail(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), new(),
                    reader.GetString(5), reader.GetString(6), new(),
                    reader.GetString(8), reader.GetString(9));
                map[id] = fk;
                order.Add(id);
            }
            fk.ParentColumns.Add(reader.GetString(4));
            fk.ReferencedColumns.Add(reader.GetString(7));
        }
        return order.Select(id => map[id]).ToList();
    }

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

    /// <summary>
    /// Renames a database from master. SQL Server requires exclusive access, so active sessions
    /// are rolled back first. If the rename fails after switching modes, MULTI_USER is restored.
    /// </summary>
    public static async Task RenameDatabaseAsync(string connectionString, string oldName, string newName)
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(WithDatabase(connectionString, "master"));
        await conn.OpenAsync();

        var oldIdentifier = QuoteIdentifier(oldName);
        var newIdentifier = QuoteIdentifier(newName);
        var singleUserSet = false;
        var activeName = oldName;
        Exception? operationError = null;
        try
        {
            await ExecuteCommandAsync(conn,
                $"ALTER DATABASE {oldIdentifier} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            singleUserSet = true;
            await ExecuteCommandAsync(conn,
                $"ALTER DATABASE {oldIdentifier} MODIFY NAME = {newIdentifier}");
            activeName = newName;
        }
        catch (Exception ex)
        {
            operationError = ex;
            throw;
        }
        finally
        {
            try
            {
                if (singleUserSet)
                    await RestoreMultiUserAsync(conn, activeName);
            }
            catch (Exception restoreError)
            {
                throw MultiUserRestoreException(activeName, operationError, restoreError);
            }
            finally
            {
                SqlConnection.ClearAllPools();
            }
        }
    }

    /// <summary>Drops a database after disconnecting active sessions from it.</summary>
    public static async Task DropDatabaseAsync(string connectionString, string database)
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(WithDatabase(connectionString, "master"));
        await conn.OpenAsync();

        var identifier = QuoteIdentifier(database);
        var singleUserSet = false;
        Exception? operationError = null;
        try
        {
            await ExecuteCommandAsync(conn,
                $"ALTER DATABASE {identifier} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            singleUserSet = true;
            await ExecuteCommandAsync(conn, $"DROP DATABASE {identifier}");
            singleUserSet = false;
        }
        catch (Exception ex)
        {
            operationError = ex;
            throw;
        }
        finally
        {
            try
            {
                if (singleUserSet)
                    await RestoreMultiUserAsync(conn, database);
            }
            catch (Exception restoreError)
            {
                throw MultiUserRestoreException(database, operationError, restoreError);
            }
            finally
            {
                SqlConnection.ClearAllPools();
            }
        }
    }

    public sealed record DatabaseCreationDefaults(
        int ProductMajorVersion,
        string RecoveryModel,
        int CompatibilityLevel,
        string DataDirectory,
        string LogDirectory,
        int DataInitialSizeMb,
        int? DataMaxSizeMb,
        int DataGrowthMb,
        int LogInitialSizeMb,
        int? LogMaxSizeMb,
        int LogGrowthMb);

    public static async Task<DatabaseCreationDefaults> GetDatabaseCreationDefaultsAsync(
        string connectionString)
    {
        await using var conn = new SqlConnection(WithDatabase(connectionString, "master"));
        await conn.OpenAsync();
        const string sql = @"
            SELECT
                CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')),
                CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath')),
                d.recovery_model_desc,
                d.compatibility_level,
                fd.physical_name,
                CONVERT(int, CEILING(fd.size * 8.0 / 1024)),
                CASE WHEN fd.max_size = -1 THEN NULL
                     ELSE CONVERT(int, CEILING(fd.max_size * 8.0 / 1024)) END,
                CASE WHEN fd.is_percent_growth = 1 THEN 64
                     ELSE CONVERT(int, CEILING(fd.growth * 8.0 / 1024)) END,
                fl.physical_name,
                CONVERT(int, CEILING(fl.size * 8.0 / 1024)),
                CASE WHEN fl.max_size = -1 THEN NULL
                     ELSE CONVERT(int, CEILING(fl.max_size * 8.0 / 1024)) END,
                CASE WHEN fl.is_percent_growth = 1 THEN 64
                     ELSE CONVERT(int, CEILING(fl.growth * 8.0 / 1024)) END
            FROM sys.databases d
            OUTER APPLY (
                SELECT TOP (1) physical_name, size, max_size, growth, is_percent_growth
                FROM sys.master_files
                WHERE database_id = d.database_id AND type = 0
                ORDER BY file_id
            ) fd
            OUTER APPLY (
                SELECT TOP (1) physical_name, size, max_size, growth, is_percent_growth
                FROM sys.master_files
                WHERE database_id = d.database_id AND type = 1
                ORDER BY file_id
            ) fl
            WHERE d.name = N'model'";
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("SQL Server did not return defaults from the model database.");

        var dataPhysicalPath = reader.IsDBNull(5) ? "" : reader.GetString(5);
        var logPhysicalPath = reader.IsDBNull(9) ? "" : reader.GetString(9);
        return new DatabaseCreationDefaults(
            reader.IsDBNull(0) ? 16 : reader.GetInt32(0),
            reader.IsDBNull(3) ? "FULL" : reader.GetString(3),
            reader.IsDBNull(4) ? 160 : reader.GetByte(4),
            reader.IsDBNull(1) ? ServerDirectory(dataPhysicalPath) : reader.GetString(1),
            reader.IsDBNull(2) ? ServerDirectory(logPhysicalPath) : reader.GetString(2),
            PositiveOrDefault(reader, 6, 8),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            PositiveOrDefault(reader, 8, 64),
            PositiveOrDefault(reader, 10, 8),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            PositiveOrDefault(reader, 12, 64));
    }

    public static async Task CreateDatabaseAsync(
        string connectionString, DatabaseCreationOptions options)
    {
        var database = options.Name;
        var sql = $"CREATE DATABASE {QuoteIdentifier(database)}";
        var hasDataFile = !string.IsNullOrWhiteSpace(options.SqlServerDataFilePath);
        var hasLogFile = !string.IsNullOrWhiteSpace(options.SqlServerLogFilePath);
        if (hasDataFile != hasLogFile)
            throw new ArgumentException("Both the data and log physical paths are required.");

        if (hasDataFile)
        {
            sql += $@"
                ON PRIMARY (
                    NAME = N'{SqlLiteral(options.SqlServerDataLogicalName ?? database)}',
                    FILENAME = N'{SqlLiteral(options.SqlServerDataFilePath!)}',
                    SIZE = {Positive(options.SqlServerDataInitialSizeMb, "data initial size")}MB,
                    MAXSIZE = {MaxSize(options.SqlServerDataMaxSizeMb)},
                    FILEGROWTH = {Positive(options.SqlServerDataGrowthMb, "data growth")}MB
                )
                LOG ON (
                    NAME = N'{SqlLiteral(options.SqlServerLogLogicalName ?? database + "_log")}',
                    FILENAME = N'{SqlLiteral(options.SqlServerLogFilePath!)}',
                    SIZE = {Positive(options.SqlServerLogInitialSizeMb, "log initial size")}MB,
                    MAXSIZE = {MaxSize(options.SqlServerLogMaxSizeMb)},
                    FILEGROWTH = {Positive(options.SqlServerLogGrowthMb, "log growth")}MB
                )";
        }

        if (!string.IsNullOrWhiteSpace(options.SqlServerCollation))
        {
            if (options.SqlServerCollation.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("The SQL Server collation name contains invalid characters.");
            sql += $" COLLATE {options.SqlServerCollation}";
        }

        await using var conn = new SqlConnection(WithDatabase(connectionString, "master"));
        await conn.OpenAsync();
        await ExecuteCommandAsync(conn, sql);
        var recoveryModel = options.SqlServerRecoveryModel.ToUpperInvariant();
        if (recoveryModel is not ("FULL" or "SIMPLE" or "BULK_LOGGED"))
            throw new ArgumentException("Invalid recovery model.");
        await ExecuteCommandAsync(conn,
            $"ALTER DATABASE {QuoteIdentifier(database)} SET RECOVERY {recoveryModel}");
        if (options.SqlServerCompatibilityLevel is < 80 or > 200
            || options.SqlServerCompatibilityLevel % 10 != 0)
            throw new ArgumentException("Invalid SQL Server compatibility level.");
        await ExecuteCommandAsync(conn,
            $"ALTER DATABASE {QuoteIdentifier(database)} SET COMPATIBILITY_LEVEL = " +
            options.SqlServerCompatibilityLevel);
        SqlConnection.ClearAllPools();
    }

    private static int Positive(int value, string field) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(field, "Value must be greater than zero.");

    private static string MaxSize(int? value) =>
        value is > 0 ? $"{value.Value}MB" : "UNLIMITED";

    private static string SqlLiteral(string value) => value.Replace("'", "''");

    private static int PositiveOrDefault(SqlDataReader reader, int ordinal, int fallback) =>
        reader.IsDBNull(ordinal) || reader.GetInt32(ordinal) <= 0 ? fallback : reader.GetInt32(ordinal);

    private static string ServerDirectory(string path)
    {
        var index = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return index < 0 ? "" : path[..(index + 1)];
    }

    private static string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

    private static async Task ExecuteCommandAsync(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RestoreMultiUserAsync(SqlConnection connection, string database)
    {
        await using var cmd = new SqlCommand(
            $"IF DB_ID(@database) IS NOT NULL " +
            $"ALTER DATABASE {QuoteIdentifier(database)} SET MULTI_USER WITH ROLLBACK IMMEDIATE",
            connection) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@database", database);
        await cmd.ExecuteNonQueryAsync();
    }

    private static InvalidOperationException MultiUserRestoreException(
        string database, Exception? operationError, Exception restoreError)
    {
        var inner = operationError is null
            ? restoreError
            : new AggregateException(operationError, restoreError);
        return new InvalidOperationException(
            $"Database '{database}' could not be restored to MULTI_USER mode. " +
            "Restore it manually before continuing to use it.",
            inner);
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
