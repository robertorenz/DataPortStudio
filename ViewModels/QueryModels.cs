using CommunityToolkit.Mvvm.ComponentModel;

namespace DataPortStudio.ViewModels;

public enum SortDirection { Asc, Desc }

/// <summary>How the cell detail panel renders the selected cell.</summary>
public enum CellViewMode { Auto, Text, Hex, Image, Web }

/// <summary>Section shown in the table structure inspector.</summary>
public enum InspectorSection { Info, Ddl, Indexes }

public enum FilterOperator
{
    Contains, Equals, NotEquals,
    GreaterThan, LessThan, GreaterOrEqual, LessOrEqual,
    StartsWith, EndsWith, IsEmpty, IsNotEmpty
}

/// <summary>One level in a multi-column sort.</summary>
public partial class SortLevel : ObservableObject
{
    [ObservableProperty] private string? column;
    [ObservableProperty] private SortDirection direction;
}

/// <summary>One condition in a filter.</summary>
public partial class FilterCondition : ObservableObject
{
    [ObservableProperty] private string? column;
    [ObservableProperty] private FilterOperator @operator;
    [ObservableProperty] private string? value;
}
