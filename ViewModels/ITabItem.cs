namespace DataPortStudio.ViewModels;

/// <summary>A tab in the content area (the Objects list, or an open table).</summary>
public interface ITabItem
{
    string Header { get; }
    bool CanClose { get; }
    /// <summary>Hover tooltip describing where the tab's content comes from.</summary>
    string TabToolTip { get; }
}
