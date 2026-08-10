using System.Text.RegularExpressions;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>Deploys a missing or changed view/routine to another endpoint of the same engine.</summary>
public static class SchemaObjectTransferService
{
    public static bool CanTransfer(
        SchemaEndpoint source, SchemaEndpoint target, TableDiff diff,
        bool sourceIsLeft, out string reason)
    {
        if (diff.ObjectType == SchemaObjectType.Table)
        {
            if (diff.Kind == DiffKind.ColumnsDiffer)
                return SchemaTableMigrationService.CanSynchronize(
                    source, target, diff, sourceIsLeft, out reason);
            var supported = TableCopyService.CanCopyBetween(source.Connection.Engine, target.Connection.Engine);
            reason = supported ? "Eligible for table copy" : "Table copy is not supported between these engines.";
            return supported;
        }

        if (source.Connection.Engine != target.Connection.Engine)
        {
            reason = "Programmable objects can only be transferred between databases of the same engine.";
            return false;
        }
        var sourceDefinition = sourceIsLeft ? diff.LeftDefinition : diff.RightDefinition;
        if (string.IsNullOrWhiteSpace(sourceDefinition))
        {
            reason = "The source definition is unavailable with the current permissions.";
            return false;
        }
        if (source.Connection.Engine == DatabaseEngine.Firebird &&
            diff.ObjectType is SchemaObjectType.Function or SchemaObjectType.Procedure)
        {
            reason = "Firebird routine parameters cannot yet be reconstructed safely from the stored source.";
            return false;
        }
        if (diff.Kind == DiffKind.DefinitionDiffers &&
            source.Connection.Engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb &&
            diff.ObjectType is SchemaObjectType.Function or SchemaObjectType.Procedure)
        {
            reason = "MySQL routines cannot be replaced atomically; delete/recreate is intentionally disabled.";
            return false;
        }

        reason = diff.Kind == DiffKind.DefinitionDiffers
            ? $"Replace the destination {diff.ObjectType.DisplayName().ToLowerInvariant()} definition"
            : $"Create the missing {diff.ObjectType.DisplayName().ToLowerInvariant()}";
        return true;
    }

    public static async Task<bool> ExistsAsync(SchemaEndpoint target, TableDiff diff)
    {
        if (diff.ObjectType == SchemaObjectType.Table)
        {
            var tables = await TableCopyService.ListObjectsAsync(
                target.Connection, target.Database, target.Schema);
            return tables.Contains(diff.TableName, StringComparer.OrdinalIgnoreCase);
        }

        var objects = await SchemaObjectMetadataService.LoadAsync(
            target, new HashSet<SchemaObjectType> { diff.ObjectType });
        return objects.Any(o => o.Type == diff.ObjectType &&
            o.Name.Equals(diff.TableName, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task TransferAsync(
        SchemaEndpoint source, SchemaEndpoint target, TableDiff diff,
        bool includeTableData, bool sourceIsLeft)
    {
        if (diff.ObjectType == SchemaObjectType.Table)
        {
            if (diff.Kind == DiffKind.ColumnsDiffer)
            {
                await SchemaTableMigrationService.SynchronizeAsync(
                    source, target, diff, sourceIsLeft);
                return;
            }
            await TableCopyService.CopyCrossAsync(
                source.Connection, source.Database, source.Schema, diff.TableName,
                target.Connection, target.Database, target.Schema, diff.TableName, includeTableData);
            return;
        }

        if (!CanTransfer(source, target, diff, sourceIsLeft, out var reason))
            throw new NotSupportedException(reason);

        var definition = sourceIsLeft ? diff.LeftDefinition : diff.RightDefinition;
        if (string.IsNullOrWhiteSpace(definition))
            throw new InvalidOperationException($"The definition of '{diff.TableName}' is unavailable.");

        var ddl = BuildTargetDdl(source, target, diff.ObjectType, diff.TableName, definition,
            replaceExisting: diff.Kind == DiffKind.DefinitionDiffers);
        var cs = target.Connection.BuildConnectionString();
        switch (target.Connection.Engine)
        {
            case DatabaseEngine.SqlServer:
                await SqlServerService.ExecuteAsync(cs, target.Database, ddl);
                break;
            case DatabaseEngine.PostgreSql:
                await PostgresService.ExecuteAsync(cs, target.Database, ddl);
                break;
            case DatabaseEngine.MySql:
            case DatabaseEngine.MariaDb:
                await MySqlService.ExecuteAsync(cs, target.Database, ddl);
                break;
            case DatabaseEngine.Sqlite:
                await SqliteService.ExecuteScriptAsync(cs, ddl);
                break;
            case DatabaseEngine.Firebird:
                await FirebirdService.ExecuteAsync(cs, ddl);
                break;
            case DatabaseEngine.Oracle:
                await OracleService.ExecuteAsync(cs, ddl);
                break;
            default:
                throw new NotSupportedException(
                    $"{target.Connection.Engine.DisplayName()} object transfer is not supported.");
        }
    }

    private static string BuildTargetDdl(
        SchemaEndpoint source, SchemaEndpoint target, SchemaObjectType type,
        string name, string definition, bool replaceExisting)
    {
        return target.Connection.Engine switch
        {
            DatabaseEngine.SqlServer => SqlServerDdl(target.Schema, type, name, definition, replaceExisting),
            DatabaseEngine.PostgreSql => PostgresDdl(source.Schema, target.Schema, type, name, definition, replaceExisting),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                MySqlDdl(source.Database, target.Database, type, name, definition, replaceExisting),
            DatabaseEngine.Sqlite => SqliteDdl(name, definition, replaceExisting),
            DatabaseEngine.Firebird => FirebirdDdl(type, name, definition, replaceExisting),
            DatabaseEngine.Oracle => OracleDdl(type, name, definition),
            _ => definition
        };
    }

    private static string SqlServerDdl(
        string schema, SchemaObjectType type, string name, string definition, bool replaceExisting)
    {
        var keyword = type switch
        {
            SchemaObjectType.View => "VIEW",
            SchemaObjectType.Function => "FUNCTION",
            _ => "PROCEDURE"
        };
        const string identifier = "(?:\\[[^\\]]+\\]|\"[^\"]+\"|[\\w@$#]+)";
        var header = new Regex(
            $@"^\s*(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+(?:PROC(?:EDURE)?|FUNCTION|VIEW)\s+{identifier}(?:\s*\.\s*{identifier})?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var quotedSchema = "[" + schema.Replace("]", "]]") + "]";
        var quotedName = "[" + name.Replace("]", "]]") + "]";
        var replacement = $"{(replaceExisting ? "ALTER" : "CREATE")} {keyword} {quotedSchema}.{quotedName}";
        return header.IsMatch(definition) ? header.Replace(definition, replacement, 1) : definition;
    }

    private static string PostgresDdl(
        string sourceSchema, string targetSchema, SchemaObjectType type, string name,
        string definition, bool replaceExisting)
    {
        var sourcePrefix = $"\"{sourceSchema.Replace("\"", "\"\"")}\".";
        var targetPrefix = $"\"{targetSchema.Replace("\"", "\"\"")}\".";
        var adjusted = definition.Replace(sourcePrefix, targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (type == SchemaObjectType.View)
            return $"CREATE{(replaceExisting ? " OR REPLACE" : "")} VIEW {targetPrefix}\"{name.Replace("\"", "\"\"")}\" AS\n{adjusted.Trim().TrimEnd(';')};";
        return adjusted;
    }

    private static string MySqlDdl(
        string sourceDatabase, string targetDatabase, SchemaObjectType type,
        string name, string definition, bool replaceExisting)
    {
        var ddl = Regex.Replace(definition,
            @"\s+DEFINER\s*=\s*(?:`[^`]*`|'[^']*'|[^\s]+)@(?:`[^`]*`|'[^']*'|[^\s]+)",
            "", RegexOptions.IgnoreCase);
        var sourcePrefix = $"`{sourceDatabase.Replace("`", "``")}`.";
        var targetPrefix = $"`{targetDatabase.Replace("`", "``")}`.";
        ddl = ddl.Replace(sourcePrefix, targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (replaceExisting && type == SchemaObjectType.View)
            ddl = Regex.Replace(ddl, @"^\s*CREATE\s+", "CREATE OR REPLACE ", RegexOptions.IgnoreCase);
        return ddl;
    }

    private static string SqliteDdl(string name, string definition, bool replaceExisting)
    {
        const string identifier = "(?:\\\"[^\\\"]+\\\"|\\[[^\\]]+\\]|`[^`]+`|[\\w$]+)";
        var create = Regex.Replace(definition,
            $@"^\s*CREATE\s+(?:TEMP\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?{identifier}",
            $"CREATE VIEW \"{name.Replace("\"", "\"\"")}\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!replaceExisting) return create;
        var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
        return $"BEGIN; DROP VIEW {quoted}; {create.Trim().TrimEnd(';')}; COMMIT;";
    }

    private static string FirebirdDdl(
        SchemaObjectType type, string name, string definition, bool replaceExisting)
    {
        if (type != SchemaObjectType.View)
            throw new NotSupportedException("Only Firebird views can currently be transferred.");
        return $"CREATE{(replaceExisting ? " OR ALTER" : "")} VIEW \"{name.Replace("\"", "\"\"")}\" AS\n{definition.Trim().TrimEnd(';')}";
    }

    private static string OracleDdl(SchemaObjectType type, string name, string definition)
    {
        if (type == SchemaObjectType.View)
            return $"CREATE OR REPLACE VIEW \"{name.Replace("\"", "\"\"")}\" AS\n{definition.Trim().TrimEnd(';')}";
        var trimmed = definition.Trim();
        return Regex.IsMatch(trimmed, @"^CREATE\b", RegexOptions.IgnoreCase)
            ? Regex.Replace(trimmed, @"^CREATE\b", "CREATE OR REPLACE", RegexOptions.IgnoreCase)
            : "CREATE OR REPLACE " + trimmed;
    }
}
