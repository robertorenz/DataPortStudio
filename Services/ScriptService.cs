using System.Data;
using System.Globalization;
using System.Text;
using DataPortStudio.Models;
using DataPortStudio.ViewModels;
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

    private static string EnsureTerminated(string script)
    {
        script = script.Trim();
        return script.EndsWith(';') ? script : script + ";";
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
