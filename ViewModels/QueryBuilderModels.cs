using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPortStudio.Models;

namespace DataPortStudio.ViewModels;

public enum JoinType { Inner, Left, Right, Full }
public enum BoolConnector { And, Or }

/// <summary>Engine-aware SQL identifier quoting for the query builder.</summary>
public static class Qb
{
    public static string Col(DatabaseEngine e, string table, string name) =>
        e == DatabaseEngine.Firebird ? $"\"{table}\".\"{name}\""
        : e.IsMySql() ? $"`{table}`.`{name}`"
        : $"[{table}].[{name}]";

    public static string From(DatabaseEngine e, string schema, string table) =>
        e == DatabaseEngine.Firebird ? $"\"{table}\""
        : e.IsMySql() ? $"`{table}`"
        : string.IsNullOrEmpty(schema) ? $"[{table}]"
        : $"[{schema}].[{table}]";
}

public partial class BuilderColumn : ObservableObject
{
    public DatabaseEngine Engine { get; init; }
    public string Table { get; init; } = "";
    public string Name { get; init; } = "";
    public string Reference => Qb.Col(Engine, Table, Name);
    [ObservableProperty] private bool included;
}

public partial class BuilderTable : ObservableObject
{
    public DatabaseEngine Engine { get; init; }
    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public string Display => string.IsNullOrEmpty(Schema) ? Table : $"{Schema}.{Table}";
    public string FromClause => Qb.From(Engine, Schema, Table);
    public ObservableCollection<BuilderColumn> Columns { get; } = new();
}

public partial class JoinRow : ObservableObject
{
    [ObservableProperty] private string? leftColumn;
    [ObservableProperty] private JoinType joinType;
    [ObservableProperty] private string? rightColumn;
}

public partial class FilterRow : ObservableObject
{
    [ObservableProperty] private BoolConnector connector;
    [ObservableProperty] private string? column;
    [ObservableProperty] private FilterOperator @operator;
    [ObservableProperty] private string? value;
}

public partial class BuilderSort : ObservableObject
{
    [ObservableProperty] private string? column;
    [ObservableProperty] private SortDirection direction;
}
