using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.ViewModels;

public enum NodeType { Server, Database, Schema, Category, Table, View, Function, Procedure, Message }

/// <summary>
/// A node in the connection tree. Children are loaded lazily the first time
/// the node is expanded. Server → Database → Schema → Category → object.
/// </summary>
public partial class DbTreeNode : ObservableObject
{
    public NodeType Type { get; private init; }
    public string Name { get; private init; } = "";
    public ConnectionProfile Connection { get; private init; } = null!;
    public string? Database { get; private init; }
    public string? Schema { get; private init; }
    /// <summary>For Category nodes: the object type its children are.</summary>
    public NodeType CategoryChildType { get; private init; }
    /// <summary>Parent node (set when children are loaded).</summary>
    public DbTreeNode? Parent { get; private set; }

    public ObservableCollection<DbTreeNode> Children { get; } = new();

    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isVisible = true;

    private bool _loaded;
    public bool IsLeaf => Type is NodeType.Table or NodeType.View or NodeType.Function
        or NodeType.Procedure or NodeType.Message;
    /// <summary>Tables and views can be opened to view their rows.</summary>
    public bool IsOpenable => Type is NodeType.Table or NodeType.View;

    /// <summary>The filter currently applied to the tree (so lazily-loaded children inherit it).</summary>
    public static string ActiveFilter { get; set; } = "";

    /// <summary>Filters this node and its loaded descendants. Returns whether this node stays visible.</summary>
    public bool ApplyFilter(string filter)
    {
        if (Type == NodeType.Message) { IsVisible = true; return false; }

        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (var c in Children) c.ApplyFilter(filter);
            return true;
        }

        if (Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            ShowAll(); // a matching parent reveals all its children
            return true;
        }

        var childMatch = false;
        foreach (var c in Children)
            if (c.Type != NodeType.Message && c.ApplyFilter(filter)) childMatch = true;

        IsVisible = childMatch;
        if (childMatch) IsExpanded = true;
        return IsVisible;
    }

    private void ShowAll()
    {
        IsVisible = true;
        foreach (var c in Children)
            if (c.Type != NodeType.Message) c.ShowAll();
    }

    // ---- factory helpers -------------------------------------------------

    public static DbTreeNode Server(ConnectionProfile c) =>
        WithPlaceholder(new DbTreeNode { Type = NodeType.Server, Name = c.Name, Connection = c });

    private static DbTreeNode DatabaseNode(ConnectionProfile c, string db) =>
        WithPlaceholder(new DbTreeNode { Type = NodeType.Database, Name = db, Connection = c, Database = db });

    private static DbTreeNode SchemaNode(ConnectionProfile c, string db, string schema) =>
        WithPlaceholder(new DbTreeNode { Type = NodeType.Schema, Name = schema, Connection = c, Database = db, Schema = schema });

    private static DbTreeNode CategoryNode(ConnectionProfile c, string db, string schema, string name, NodeType childType) =>
        WithPlaceholder(new DbTreeNode { Type = NodeType.Category, Name = name, Connection = c, Database = db, Schema = schema, CategoryChildType = childType });

    private static DbTreeNode ObjectNode(NodeType type, ConnectionProfile c, string db, string schema, string name) =>
        new() { Type = type, Name = name, Connection = c, Database = db, Schema = schema };

    /// <summary>Creates an object child of this container (used by the object-list view).</summary>
    public DbTreeNode MakeObjectChild(NodeType type, string name, string? schemaOverride = null)
    {
        var db = Type == NodeType.Database ? Name : Database;
        string? schema;
        if (schemaOverride is not null)
            schema = schemaOverride;
        else if (Type == NodeType.Database)
            schema = Connection.Engine == DatabaseEngine.MongoDb ? Name : "dbo";
        else
            schema = Schema;
        var node = ObjectNode(type, Connection, db ?? "", schema ?? "", name);
        node.Parent = this;
        return node;
    }

    private static DbTreeNode Message(string text) =>
        new() { Type = NodeType.Message, Name = text };

    private static DbTreeNode WithPlaceholder(DbTreeNode node)
    {
        // A dummy child makes the expander arrow appear before real children load.
        node.Children.Add(Message("Loading…"));
        return node;
    }

    // ---- lazy loading ----------------------------------------------------

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && !IsLeaf)
            _ = LoadChildrenAsync();
    }

    public void Reset()
    {
        _loaded = false;
        HasError = false;
        Children.Clear();
        if (!IsLeaf) Children.Add(Message("Loading…"));
    }

    public async Task LoadChildrenAsync()
    {
        if (IsLeaf) return;
        _loaded = true;
        IsLoading = true;
        HasError = false;
        try
        {
            if (!Connection.Engine.IsSupported())
            {
                Children.Clear();
                Children.Add(Message($"{Connection.Engine.DisplayName()} support is coming soon."));
                return;
            }

            var connStr = Connection.BuildConnectionString();
            var items = Connection.Engine switch
            {
                DatabaseEngine.Sqlite => await LoadSqliteChildrenAsync(connStr),
                DatabaseEngine.Firebird => await LoadFirebirdChildrenAsync(connStr),
                DatabaseEngine.MongoDb => await LoadMongoChildrenAsync(connStr),
                DatabaseEngine.Tps => LoadClarionFileChildren(TpsService.ListTables(Connection.FilePath)),
                DatabaseEngine.ClarionDat => LoadClarionFileChildren(DatService.ListTables(Connection.FilePath)),
                DatabaseEngine.Excel => LoadExcelFileChildren(ExcelService.ListFiles(Connection.FilePath)),
                DatabaseEngine.MySql or DatabaseEngine.MariaDb => await LoadMySqlChildrenAsync(connStr),
                DatabaseEngine.Oracle => await LoadOracleChildrenAsync(connStr),
                _ => await LoadSqlServerChildrenAsync(connStr)
            };

            Children.Clear();
            if (items.Count == 0)
                Children.Add(Message("(empty)"));
            else
                foreach (var n in items) { n.Parent = this; Children.Add(n); }

            // Apply the active filter to freshly-loaded children.
            if (!string.IsNullOrWhiteSpace(ActiveFilter))
                foreach (var n in items) n.ApplyFilter(ActiveFilter);
        }
        catch (Exception ex)
        {
            HasError = true;
            _loaded = false;
            Children.Clear();
            Children.Add(Message(ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<List<DbTreeNode>> LoadSqlServerChildrenAsync(string connStr)
    {
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                // With a default database set, skip the database level: connection → schema → tables.
                if (!string.IsNullOrWhiteSpace(Connection.Database))
                {
                    var db = Connection.Database!;
                    foreach (var schema in await SqlServerService.GetSchemasAsync(connStr, db))
                        items.Add(SchemaNode(Connection, db, schema));
                }
                else
                {
                    foreach (var db in await SqlServerService.GetDatabasesAsync(connStr))
                        items.Add(DatabaseNode(Connection, db));
                }
                break;
            case NodeType.Database:
                foreach (var schema in await SqlServerService.GetSchemasAsync(connStr, Database!))
                    items.Add(SchemaNode(Connection, Database!, schema));
                break;
            case NodeType.Schema:
                items.Add(CategoryNode(Connection, Database!, Schema!, "Tables", NodeType.Table));
                items.Add(CategoryNode(Connection, Database!, Schema!, "Views", NodeType.View));
                items.Add(CategoryNode(Connection, Database!, Schema!, "Functions", NodeType.Function));
                items.Add(CategoryNode(Connection, Database!, Schema!, "Procedures", NodeType.Procedure));
                break;
            case NodeType.Category:
                var names = CategoryChildType switch
                {
                    NodeType.Table => await SqlServerService.GetTablesAsync(connStr, Database!, Schema!),
                    NodeType.View => await SqlServerService.GetViewsAsync(connStr, Database!, Schema!),
                    NodeType.Function => await SqlServerService.GetFunctionsAsync(connStr, Database!, Schema!),
                    NodeType.Procedure => await SqlServerService.GetProceduresAsync(connStr, Database!, Schema!),
                    _ => new List<string>()
                };
                foreach (var n in names)
                    items.Add(ObjectNode(CategoryChildType, Connection, Database!, Schema!, n));
                break;
        }
        return items;
    }

    /// <summary>SQLite has a single database file with no schemas — show Tables/Views directly.</summary>
    private async Task<List<DbTreeNode>> LoadSqliteChildrenAsync(string connStr)
    {
        const string main = "main";
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                items.Add(CategoryNode(Connection, main, main, "Tables", NodeType.Table));
                items.Add(CategoryNode(Connection, main, main, "Views", NodeType.View));
                break;
            case NodeType.Category:
                var names = CategoryChildType switch
                {
                    NodeType.Table => await SqliteService.GetTablesAsync(connStr),
                    NodeType.View => await SqliteService.GetViewsAsync(connStr),
                    _ => new List<string>()
                };
                foreach (var n in names)
                    items.Add(ObjectNode(CategoryChildType, Connection, main, main, n));
                break;
        }
        return items;
    }

    /// <summary>MySQL/MariaDB: Server → databases → Tables/Views/Functions/Procedures (db == schema).</summary>
    private async Task<List<DbTreeNode>> LoadMySqlChildrenAsync(string connStr)
    {
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                foreach (var db in await MySqlService.GetDatabasesAsync(connStr))
                    items.Add(DatabaseNode(Connection, db));
                break;
            case NodeType.Database:
                items.Add(CategoryNode(Connection, Name, Name, "Tables", NodeType.Table));
                items.Add(CategoryNode(Connection, Name, Name, "Views", NodeType.View));
                items.Add(CategoryNode(Connection, Name, Name, "Functions", NodeType.Function));
                items.Add(CategoryNode(Connection, Name, Name, "Procedures", NodeType.Procedure));
                break;
            case NodeType.Category:
                var names = CategoryChildType switch
                {
                    NodeType.Table => await MySqlService.GetTablesAsync(connStr, Database!),
                    NodeType.View => await MySqlService.GetViewsAsync(connStr, Database!),
                    NodeType.Function => await MySqlService.GetFunctionsAsync(connStr, Database!),
                    NodeType.Procedure => await MySqlService.GetProceduresAsync(connStr, Database!),
                    _ => new List<string>()
                };
                foreach (var n in names)
                    items.Add(ObjectNode(CategoryChildType, Connection, Database!, Schema!, n));
                break;
        }
        return items;
    }

    /// <summary>Oracle: browse the connected user's own schema — Tables/Views/Functions/Procedures.</summary>
    private async Task<List<DbTreeNode>> LoadOracleChildrenAsync(string connStr)
    {
        const string ora = "oracle";
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                items.Add(CategoryNode(Connection, ora, ora, "Tables", NodeType.Table));
                items.Add(CategoryNode(Connection, ora, ora, "Views", NodeType.View));
                break;
            case NodeType.Category:
                var names = CategoryChildType switch
                {
                    NodeType.Table => await OracleService.GetTablesAsync(connStr),
                    NodeType.View => await OracleService.GetViewsAsync(connStr),
                    _ => new List<string>()
                };
                foreach (var n in names)
                    items.Add(ObjectNode(CategoryChildType, Connection, ora, ora, n));
                break;
        }
        return items;
    }

    /// <summary>Firebird connects to a single database — show Tables/Views directly.</summary>
    private async Task<List<DbTreeNode>> LoadFirebirdChildrenAsync(string connStr)
    {
        const string fb = "firebird";
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                items.Add(CategoryNode(Connection, fb, fb, "Tables", NodeType.Table));
                items.Add(CategoryNode(Connection, fb, fb, "Views", NodeType.View));
                break;
            case NodeType.Category:
                var names = CategoryChildType switch
                {
                    NodeType.Table => await FirebirdService.GetTablesAsync(connStr),
                    NodeType.View => await FirebirdService.GetViewsAsync(connStr),
                    _ => new List<string>()
                };
                foreach (var n in names)
                    items.Add(ObjectNode(CategoryChildType, Connection, fb, fb, n));
                break;
        }
        return items;
    }

    /// <summary>Clarion flat files (TPS/DAT): the connection is a folder — each file is a table.</summary>
    private List<DbTreeNode> LoadClarionFileChildren(List<string> names)
    {
        var items = new List<DbTreeNode>();
        if (Type == NodeType.Server)
            foreach (var name in names)
                items.Add(ObjectNode(NodeType.Table, Connection, "", "", name));
        return items;
    }

    /// <summary>Excel folder: one node per file. Opening the file node opens all its sheets as separate tabs.</summary>
    private List<DbTreeNode> LoadExcelFileChildren(List<string> fileNames)
    {
        var items = new List<DbTreeNode>();
        if (Type == NodeType.Server)
            foreach (var fileName in fileNames)
                items.Add(ObjectNode(NodeType.Table, Connection, fileName, "", fileName));
        return items;
    }

    /// <summary>Creates a sheet tab node from an Excel file node. Schema = sheetName, Database = fileName.</summary>
    public static DbTreeNode ExcelSheetNode(ConnectionProfile connection, string fileName, string sheetName, string displayName) =>
        new() { Type = NodeType.Table, Name = displayName, Connection = connection, Database = fileName, Schema = sheetName };

    /// <summary>MongoDB: Server → databases → collections (shown as table nodes).</summary>
    private async Task<List<DbTreeNode>> LoadMongoChildrenAsync(string uri)
    {
        var items = new List<DbTreeNode>();
        switch (Type)
        {
            case NodeType.Server:
                foreach (var db in await MongoService.ListDatabasesAsync(uri))
                    items.Add(DatabaseNode(Connection, db));
                break;
            case NodeType.Database:
                foreach (var c in await MongoService.ListCollectionsAsync(uri, Database!))
                    items.Add(ObjectNode(NodeType.Table, Connection, Database!, Database!, c));
                break;
        }
        return items;
    }
}
