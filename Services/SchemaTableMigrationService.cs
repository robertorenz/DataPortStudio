using System.Text;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>
/// Applies non-destructive column changes to an existing SQL Server table. Column drops and
/// physical reordering are intentionally excluded because they require a table-rebuild workflow.
/// </summary>
public static class SchemaTableMigrationService
{
    public static bool CanSynchronize(
        SchemaEndpoint source, SchemaEndpoint target, TableDiff diff,
        bool sourceIsLeft, out string reason)
    {
        if (diff.Kind != DiffKind.ColumnsDiffer || diff.ObjectType != SchemaObjectType.Table)
        {
            reason = "This is not an existing table with column differences.";
            return false;
        }
        if (source.Connection.Engine != DatabaseEngine.SqlServer ||
            target.Connection.Engine != DatabaseEngine.SqlServer)
        {
            reason = "Existing-column synchronization is currently available for SQL Server only.";
            return false;
        }

        foreach (var column in diff.ColumnDiffs)
        {
            var sourceColumn = sourceIsLeft ? column.Left : column.Right;
            var targetColumn = sourceIsLeft ? column.Right : column.Left;
            if (sourceColumn is null && targetColumn is not null)
            {
                reason = $"Would need to drop destination column '{column.Name}', which is disabled to prevent data loss.";
                return false;
            }
            if (column.OrderDiffers)
            {
                reason = $"Column '{column.Name}' requires physical reordering, which needs a table rebuild.";
                return false;
            }
        }

        var additions = diff.ColumnDiffs.Count(c =>
            (sourceIsLeft ? c.Left : c.Right) is not null &&
            (sourceIsLeft ? c.Right : c.Left) is null);
        var alterations = diff.ColumnDiffs.Count(c =>
            (sourceIsLeft ? c.Left : c.Right) is not null &&
            (sourceIsLeft ? c.Right : c.Left) is not null);
        reason = $"Apply {additions} column addition(s) and {alterations} column alteration(s) in one transaction.";
        return additions + alterations > 0;
    }

    public static async Task SynchronizeAsync(
        SchemaEndpoint source, SchemaEndpoint target, TableDiff diff, bool sourceIsLeft)
    {
        if (!CanSynchronize(source, target, diff, sourceIsLeft, out var reason))
            throw new NotSupportedException(reason);

        static string Q(string identifier) => "[" + identifier.Replace("]", "]]" ) + "]";
        var table = $"{Q(target.Schema)}.{Q(diff.TableName)}";
        var statements = new List<string>();
        foreach (var column in diff.ColumnDiffs)
        {
            var sourceColumn = sourceIsLeft ? column.Left : column.Right;
            var targetColumn = sourceIsLeft ? column.Right : column.Left;
            if (sourceColumn is null) continue;
            var nullability = sourceColumn.IsNullable ? "NULL" : "NOT NULL";
            statements.Add(targetColumn is null
                ? $"ALTER TABLE {table} ADD {Q(sourceColumn.Name)} {sourceColumn.DataType} {nullability};"
                : $"ALTER TABLE {table} ALTER COLUMN {Q(sourceColumn.Name)} {sourceColumn.DataType} {nullability};");
        }

        var sql = new StringBuilder()
            .AppendLine("SET XACT_ABORT ON;")
            .AppendLine("BEGIN TRANSACTION;")
            .AppendLine("BEGIN TRY")
            .AppendLine(string.Join(Environment.NewLine, statements.Select(s => "    " + s)))
            .AppendLine("    COMMIT TRANSACTION;")
            .AppendLine("END TRY")
            .AppendLine("BEGIN CATCH")
            .AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;")
            .AppendLine("    THROW;")
            .AppendLine("END CATCH;")
            .ToString();
        await SqlServerService.ExecuteAsync(
            target.Connection.BuildConnectionString(), target.Database, sql);
    }
}
