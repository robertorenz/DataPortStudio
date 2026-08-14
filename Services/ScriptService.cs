using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DataPortStudio.Models;
using DataPortStudio.ViewModels;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DataPortStudio.Services;

/// <summary>Generates SQL scripts (e.g. INSERT statements) from table data.</summary>
public static class ScriptService
{
    public sealed record DatabaseScript(string Text, string Extension);

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

    public static async Task<string> GenerateObjectScriptAsync(DbTreeNode node)
    {
        var connection = node.Connection;
        var cs = connection.BuildConnectionString();
        var database = node.Database ?? "";
        var schema = node.Schema ?? "dbo";

        if (node.Type == NodeType.Table)
        {
            var structure = await TableMetadataService.GetAsync(
                connection.Engine, cs, database, schema, node.Name, connection.Name);
            return EnsureTerminated(structure.Ddl);
        }

        string? definition = connection.Engine switch
        {
            DatabaseEngine.SqlServer => await SqlServerService.GetObjectDefinitionAsync(
                cs, database, schema, node.Name),
            DatabaseEngine.Sqlite => await SqliteService.GetObjectDefinitionAsync(cs, node.Name),
            DatabaseEngine.Firebird when node.Type == NodeType.View =>
                await FirebirdService.GetViewDefinitionAsync(cs, node.Name),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                await MySqlService.GetObjectDefinitionAsync(cs, database, node.Type, node.Name),
            DatabaseEngine.PostgreSql =>
                await PostgresService.GetObjectDefinitionAsync(cs, database, schema, node.Type, node.Name),
            DatabaseEngine.Oracle =>
                await OracleService.GetObjectDefinitionAsync(cs, node.Type, node.Name),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(definition))
            throw new InvalidOperationException(
                LocalizationManager.Instance["Script_DefinitionUnavailable"]);
        return EnsureTerminated(definition);
    }

    /// <summary>Generates a structure-only script for every user object in a database.</summary>
    public static async Task<DatabaseScript> GenerateDatabaseScriptAsync(
        ConnectionProfile connection, string database)
    {
        var cs = connection.BuildConnectionString();
        var sb = new StringBuilder();
        sb.AppendLine($"-- DataPortStudio database script");
        sb.AppendLine($"-- Engine: {connection.Engine.DisplayName()}");
        sb.AppendLine($"-- Database: {database}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        switch (connection.Engine)
        {
            case DatabaseEngine.SqlServer:
                await ScriptSqlServerDatabaseAsync(connection, database, sb);
                break;
            case DatabaseEngine.PostgreSql:
                await ScriptPostgresDatabaseAsync(connection, database, sb);
                break;
            case DatabaseEngine.MySql:
            case DatabaseEngine.MariaDb:
                await ScriptMySqlDatabaseAsync(connection, database, sb);
                break;
            case DatabaseEngine.Sqlite:
                sb.AppendLine("PRAGMA foreign_keys = OFF;");
                await ScriptFlatDatabaseAsync(connection, database, "main", sb,
                    await SqliteService.GetTablesAsync(cs), await SqliteService.GetViewsAsync(cs));
                sb.AppendLine("PRAGMA foreign_keys = ON;");
                break;
            case DatabaseEngine.Firebird:
                await ScriptFlatDatabaseAsync(connection, database, "firebird", sb,
                    await FirebirdService.GetTablesAsync(cs), await FirebirdService.GetViewsAsync(cs));
                break;
            case DatabaseEngine.Oracle:
                await ScriptOracleDatabaseAsync(connection, database, sb);
                break;
            case DatabaseEngine.MongoDb:
                await ScriptMongoDatabaseAsync(connection, database, sb);
                return new DatabaseScript(sb.ToString(), ".js");
            default:
                throw new NotSupportedException();
        }

        return new DatabaseScript(sb.ToString(), ".sql");
    }

    private static async Task ScriptSqlServerDatabaseAsync(
        ConnectionProfile connection, string database, StringBuilder sb)
    {
        var cs = connection.BuildConnectionString();
        sb.AppendLine($"IF DB_ID(N'{database.Replace("'", "''")}') IS NULL CREATE DATABASE {Quote(database)};");
        sb.AppendLine("GO");
        sb.AppendLine($"USE {Quote(database)};");
        sb.AppendLine("GO");

        foreach (var schema in await SqlServerService.GetSchemasAsync(cs, database))
        {
            if (!schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"IF SCHEMA_ID(N'{schema.Replace("'", "''")}') IS NULL EXEC(N'CREATE SCHEMA {Quote(schema)}');");
                sb.AppendLine("GO");
            }
            await AppendObjectsAsync(connection, database, schema, NodeType.Table,
                await SqlServerService.GetTablesAsync(cs, database, schema), sb, addGo: true);
            await AppendObjectsAsync(connection, database, schema, NodeType.Function,
                await SqlServerService.GetFunctionsAsync(cs, database, schema), sb, addGo: true);
            await AppendObjectsAsync(connection, database, schema, NodeType.View,
                await SqlServerService.GetViewsAsync(cs, database, schema), sb, addGo: true);
            await AppendObjectsAsync(connection, database, schema, NodeType.Procedure,
                await SqlServerService.GetProceduresAsync(cs, database, schema), sb, addGo: true);
        }
    }

    private static async Task ScriptPostgresDatabaseAsync(
        ConnectionProfile connection, string database, StringBuilder sb)
    {
        var cs = connection.BuildConnectionString();
        sb.AppendLine($"-- Run CREATE DATABASE {PostgresService.Quote(database)} separately if it does not exist.");
        sb.AppendLine($"\\connect {PostgresService.Quote(database)}");
        foreach (var schema in await PostgresService.GetSchemasAsync(cs, database))
        {
            sb.AppendLine($"CREATE SCHEMA IF NOT EXISTS {PostgresService.Quote(schema)};");
            await AppendObjectsAsync(connection, database, schema, NodeType.Table,
                await PostgresService.GetTablesAsync(cs, database, schema), sb);
            await AppendObjectsAsync(connection, database, schema, NodeType.Function,
                await PostgresService.GetFunctionsAsync(cs, database, schema), sb);
            await AppendObjectsAsync(connection, database, schema, NodeType.View,
                await PostgresService.GetViewsAsync(cs, database, schema), sb);
            await AppendObjectsAsync(connection, database, schema, NodeType.Procedure,
                await PostgresService.GetProceduresAsync(cs, database, schema), sb);
        }
    }

    private static async Task ScriptMySqlDatabaseAsync(
        ConnectionProfile connection, string database, StringBuilder sb)
    {
        var cs = connection.BuildConnectionString();
        sb.AppendLine($"CREATE DATABASE IF NOT EXISTS {MySqlService.Quote(database)};");
        sb.AppendLine($"USE {MySqlService.Quote(database)};");
        await AppendObjectsAsync(connection, database, database, NodeType.Table,
            await MySqlService.GetTablesAsync(cs, database), sb);
        await AppendObjectsAsync(connection, database, database, NodeType.Function,
            await MySqlService.GetFunctionsAsync(cs, database), sb);
        await AppendObjectsAsync(connection, database, database, NodeType.View,
            await MySqlService.GetViewsAsync(cs, database), sb);
        await AppendObjectsAsync(connection, database, database, NodeType.Procedure,
            await MySqlService.GetProceduresAsync(cs, database), sb);
    }

    private static async Task ScriptOracleDatabaseAsync(
        ConnectionProfile connection, string database, StringBuilder sb)
    {
        var cs = connection.BuildConnectionString();
        var schema = connection.Username ?? database;
        await AppendObjectsAsync(connection, database, schema, NodeType.Table,
            await OracleService.GetTablesAsync(cs), sb);
        await AppendObjectsAsync(connection, database, schema, NodeType.Function,
            await OracleService.GetFunctionsAsync(cs), sb);
        await AppendObjectsAsync(connection, database, schema, NodeType.View,
            await OracleService.GetViewsAsync(cs), sb);
        await AppendObjectsAsync(connection, database, schema, NodeType.Procedure,
            await OracleService.GetProceduresAsync(cs), sb);
    }

    private static async Task ScriptFlatDatabaseAsync(
        ConnectionProfile connection, string database, string schema, StringBuilder sb,
        IReadOnlyList<string> tables, IReadOnlyList<string> views)
    {
        await AppendObjectsAsync(connection, database, schema, NodeType.Table, tables, sb);
        await AppendObjectsAsync(connection, database, schema, NodeType.View, views, sb);
    }

    private static async Task AppendObjectsAsync(
        ConnectionProfile connection, string database, string schema, NodeType type,
        IReadOnlyList<string> names, StringBuilder sb, bool addGo = false)
    {
        foreach (var name in names)
        {
            try
            {
                var definition = type == NodeType.Table
                    ? (await TableMetadataService.GetAsync(connection.Engine,
                        connection.BuildConnectionString(), database, schema, name, connection.Name)).Ddl
                    : await GetDefinitionAsync(connection, database, schema, type, name);
                if (string.IsNullOrWhiteSpace(definition))
                    throw new InvalidOperationException(
                        LocalizationManager.Instance["Script_DefinitionUnavailable"]);
                var normalized = EnsureTerminated(definition);
                sb.AppendLine(normalized);
                if (addGo && !EndsWithGo(normalized)) sb.AppendLine("GO");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"-- Could not generate {type.ToString().ToLowerInvariant()} {schema}.{name}: {ex.Message.Replace("\r", " ").Replace("\n", " ")}");
            }
        }
    }

    private static Task<string?> GetDefinitionAsync(
        ConnectionProfile connection, string database, string schema, NodeType type, string name)
    {
        var cs = connection.BuildConnectionString();
        return connection.Engine switch
        {
            DatabaseEngine.SqlServer => SqlServerService.GetObjectDefinitionAsync(cs, database, schema, name),
            DatabaseEngine.Sqlite => SqliteService.GetObjectDefinitionAsync(cs, name),
            DatabaseEngine.Firebird when type == NodeType.View => FirebirdService.GetViewDefinitionAsync(cs, name),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                MySqlService.GetObjectDefinitionAsync(cs, database, type, name),
            DatabaseEngine.PostgreSql =>
                PostgresService.GetObjectDefinitionAsync(cs, database, schema, type, name),
            DatabaseEngine.Oracle => OracleService.GetObjectDefinitionAsync(cs, type, name),
            _ => Task.FromResult<string?>(null)
        };
    }

    private static async Task ScriptMongoDatabaseAsync(
        ConnectionProfile connection, string database, StringBuilder sb)
    {
        var mongo = new MongoClient(connection.BuildConnectionString()).GetDatabase(database);
        sb.AppendLine($"db = db.getSiblingDB({JsonSerializer.Serialize(database)});");
        foreach (var collectionName in await MongoService.ListCollectionsAsync(
                     connection.BuildConnectionString(), database))
        {
            var quotedName = JsonSerializer.Serialize(collectionName);
            sb.AppendLine($"if (!db.getCollectionNames().includes({quotedName})) db.createCollection({quotedName});");

            var collection = mongo.GetCollection<BsonDocument>(collectionName);
            using var cursor = await collection.Indexes.ListAsync();
            while (await cursor.MoveNextAsync())
            {
                foreach (var index in cursor.Current)
                {
                    if (index.TryGetValue("name", out var indexName) && indexName.AsString == "_id_")
                        continue;
                    var clean = new BsonDocument(index.Where(element =>
                        element.Name is not "v" and not "ns"));
                    sb.AppendLine($"db.runCommand({{ createIndexes: {quotedName}, indexes: [{clean.ToJson()}] }});");
                }
            }
            sb.AppendLine();
        }
    }

    private static string EnsureTerminated(string script)
    {
        script = script.Trim();
        return script.EndsWith(';') || EndsWithGo(script) ? script : script + ";";
    }

    private static bool EndsWithGo(string script) =>
        string.Equals(script.TrimEnd().Split('\n').Last().Trim(), "GO",
            StringComparison.OrdinalIgnoreCase);

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
