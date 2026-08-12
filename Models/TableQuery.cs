namespace DataPortStudio.Models;

/// <summary>A database-side filter operator used by the table data browser.</summary>
public enum TableFilterOperator
{
    Contains, Equals, NotEquals,
    GreaterThan, LessThan, GreaterOrEqual, LessOrEqual,
    StartsWith, EndsWith, IsEmpty, IsNotEmpty
}

/// <summary>One parameterized predicate in a table-browser query.</summary>
public sealed record TableQueryFilter(
    string Column,
    TableFilterOperator Operator,
    object? Value,
    bool IsString);

/// <summary>One ORDER BY level in a table-browser query.</summary>
public sealed record TableQuerySort(string Column, bool Descending);

/// <summary>
/// Filter and sort state sent to the database before the browser's row limit is applied.
/// </summary>
public sealed record TableQuery(
    IReadOnlyList<TableQueryFilter> Filters,
    bool MatchAll,
    IReadOnlyList<TableQuerySort> Sorts)
{
    public static TableQuery Empty { get; } = new([], true, []);
}
