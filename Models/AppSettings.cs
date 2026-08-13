using DataPortStudio.ViewModels;

namespace DataPortStudio.Models;

public enum ObjectSearchMode { Locator, Filter }
public enum QueryOpenMode { Window, Docked }

/// <summary>User-configurable application defaults.</summary>
public class AppSettings
{
    public int DefaultRowLimit { get; set; } = 1000;
    public bool ShowStructureByDefault { get; set; }
    public bool ShowSqlByDefault { get; set; }
    public bool ShowCellDetailByDefault { get; set; }
    public InspectorSection DefaultStructureSection { get; set; } = InspectorSection.Ddl;
    public bool ShowClarionTypesByDefault { get; set; } = true;

    /// <summary>UI language code: "en" or "es".</summary>
    public string Language { get; set; } = "en";

    /// <summary>UI theme: "light" or "dark".</summary>
    public string Theme { get; set; } = "light";

    /// <summary>How the Objects search box behaves: move to matches or hide non-matches.</summary>
    public ObjectSearchMode ObjectSearchMode { get; set; } = ObjectSearchMode.Filter;

    /// <summary>Where SQL query editors open.</summary>
    public QueryOpenMode QueryOpenMode { get; set; } = QueryOpenMode.Window;

    /// <summary>Last active database per connection (connectionId → databaseName).</summary>
    public Dictionary<string, string> LastDatabases { get; set; } = new();

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.LastDatabases = new Dictionary<string, string>(LastDatabases);
        return c;
    }
}
