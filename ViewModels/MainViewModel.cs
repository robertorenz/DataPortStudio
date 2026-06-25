using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;
using DataPortStudio.Views;

namespace DataPortStudio.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConnectionStore _store = new();

    public ObservableCollection<DbTreeNode> Roots { get; } = new();

    /// <summary>All content tabs: the persistent Objects tab (when present) plus open tables.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    [ObservableProperty] private DbTreeNode? selectedNode;
    [ObservableProperty] private object? activeTab;
    [ObservableProperty] private string treeFilter = "";
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private bool isBusy;

    /// <summary>The currently-selected table tab (null when the Objects tab is active).</summary>
    public TableTabViewModel? SelectedTab => ActiveTab as TableTabViewModel;

    /// <summary>The single, persistent Objects tab (created on first use).</summary>
    private ObjectListViewModel? _objectsTab;

    public bool ShowEmpty => Tabs.Count == 0;

    partial void OnActiveTabChanged(object? value) => OnPropertyChanged(nameof(SelectedTab));

    partial void OnSelectedNodeChanged(DbTreeNode? value)
    {
        _ = UpdateObjectListAsync(value);
        SaveLastDatabase(value);
    }

    private static void SaveLastDatabase(DbTreeNode? node)
    {
        if (node is null) return;
        var db = node.Database ?? (node.Type == NodeType.Database ? node.Name : null);
        if (string.IsNullOrEmpty(db)) return;

        var settings = SettingsStore.Current;
        var key = node.Connection.Id.ToString();
        if (settings.LastDatabases.TryGetValue(key, out var existing) && existing == db) return;

        settings.LastDatabases[key] = db;
        SettingsStore.Save(settings);
    }

    private async Task UpdateObjectListAsync(DbTreeNode? node)
    {
        // The Objects tab tracks the selected Tables/Views/Functions folder, schema, or database.
        // TPS connections are flat (no folders), so the Server node itself lists the .tps files.
        var show = node is { Type: NodeType.Category, CategoryChildType: NodeType.Table or NodeType.View
                                 or NodeType.Function or NodeType.Procedure }
                   or { Type: NodeType.Schema }
                   or { Type: NodeType.Database }
                   or { Type: NodeType.Server, Connection.Engine: DatabaseEngine.Tps or DatabaseEngine.ClarionDat };
        if (!show) return;

        _objectsTab ??= new ObjectListViewModel(
            open: OpenFromList,
            design: (c, item) => DesignTableCommand.Execute(NodeForItem(c, item)),
            delete: DeleteFromListAsync,
            @new: c => NewTableCommand.Execute(c),
            copy: (c, item) => CopyTableCommand.Execute(NodeForItem(c, item)),
            paste: PasteFromListAsync);

        if (!Tabs.Contains(_objectsTab))
        {
            Tabs.Insert(0, _objectsTab);
            OnPropertyChanged(nameof(ShowEmpty));
        }
        ActiveTab = _objectsTab;
        await _objectsTab.ConfigureAsync(node!);
    }

    private static NodeType ChildTypeOf(DbTreeNode container) =>
        container.Type == NodeType.Category ? container.CategoryChildType : NodeType.Table;

    private static DbTreeNode NodeForItem(DbTreeNode container, ObjectListItem item) =>
        container.MakeObjectChild(ChildTypeOf(container), item.Name, item.Schema);

    private void OpenFromList(DbTreeNode container, ObjectListItem item)
    {
        var node = NodeForItem(container, item);
        if (node.IsOpenable) OpenTableCommand.Execute(node);          // table / view
        else EditRoutineCommand.Execute(node);                        // function / procedure
    }

    private async void DeleteFromListAsync(DbTreeNode container, ObjectListItem item)
    {
        var node = NodeForItem(container, item);
        if (node.Type == NodeType.Table) await DropTable(node);
        else await DropRoutine(node);
        if (_objectsTab is not null) await _objectsTab.LoadAsync();
    }

    private async void PasteFromListAsync(DbTreeNode container)
    {
        // PasteTable already refreshes the tree folder and the Objects list.
        await PasteTable(container);
    }

    // ---- command-bar navigation -----------------------------------------

    [RelayCommand]
    private Task GoToTables() => GoToSection(NodeType.Table);
    [RelayCommand]
    private Task GoToViews() => GoToSection(NodeType.View);
    [RelayCommand]
    private Task GoToFunctions() => GoToSection(NodeType.Function);
    [RelayCommand]
    private Task GoToProcedures() => GoToSection(NodeType.Procedure);

    /// <summary>Drills the tree to the current connection/database's section and selects it.</summary>
    private async Task GoToSection(NodeType childType)
    {
        var conn = SelectedNode?.Connection ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (conn is null) { StatusText = "Add a connection first."; return; }
        var server = Roots.FirstOrDefault(r => r.Type == NodeType.Server && r.Connection.Id == conn.Id);
        if (server is null) return;

        try
        {
            var target = await FindSectionNodeAsync(server, SelectedNode, childType);
            if (target is null) { StatusText = "That section isn't available for this connection."; return; }

            for (var n = target.Parent; n is not null; n = n.Parent) n.IsExpanded = true;
            target.IsExpanded = true;
            SelectedNode = target; // shows the object list for the section
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't open the section: " + ex.Message;
        }
    }

    private static async Task<DbTreeNode?> FindSectionNodeAsync(DbTreeNode server, DbTreeNode? selected, NodeType childType)
    {
        await server.LoadChildrenAsync();
        var engine = server.Connection.Engine;

        if (engine is DatabaseEngine.Sqlite or DatabaseEngine.Firebird)
            return server.Children.FirstOrDefault(c => c.Type == NodeType.Category && c.CategoryChildType == childType);

        if (engine == DatabaseEngine.MongoDb)
            return childType == NodeType.Table
                ? (selected?.Type == NodeType.Database ? selected : server.Children.FirstOrDefault(c => c.Type == NodeType.Database))
                : null;

        if (engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb)
        {
            // MySQL/MariaDB: server children are Database nodes; each holds the categories (db == schema).
            var myDb = (selected?.Database is { } sd ? server.Children.FirstOrDefault(c => c.Type == NodeType.Database && c.Name == sd) : null)
                       ?? (selected?.Type == NodeType.Database ? selected : null)
                       ?? server.Children.FirstOrDefault(c => c.Type == NodeType.Database);
            if (myDb is null) return null;
            await myDb.LoadChildrenAsync();
            return myDb.Children.FirstOrDefault(c => c.Type == NodeType.Category && c.CategoryChildType == childType);
        }

        // SQL Server: server children are either Schema nodes (default-db layout) or Database nodes.
        DbTreeNode? schema;
        if (server.Children.Any(c => c.Type == NodeType.Schema))
        {
            schema = PickSchema(server, selected?.Schema);
        }
        else
        {
            var db = (selected?.Database is { } d ? server.Children.FirstOrDefault(c => c.Type == NodeType.Database && c.Name == d) : null)
                     ?? server.Children.FirstOrDefault(c => c.Type == NodeType.Database);
            if (db is null) return null;
            await db.LoadChildrenAsync();
            schema = PickSchema(db, selected?.Schema);
        }
        if (schema is null) return null;
        await schema.LoadChildrenAsync();
        return schema.Children.FirstOrDefault(c => c.Type == NodeType.Category && c.CategoryChildType == childType);
    }

    private static DbTreeNode? PickSchema(DbTreeNode parent, string? preferred) =>
        (preferred is { } s ? parent.Children.FirstOrDefault(c => c.Type == NodeType.Schema && c.Name == s) : null)
        ?? parent.Children.FirstOrDefault(c => c.Type == NodeType.Schema && c.Name == "dbo")
        ?? parent.Children.FirstOrDefault(c => c.Type == NodeType.Schema);

    /// <summary>Row limit applied when opening a new tab.</summary>

    public MainViewModel()
    {
        foreach (var profile in _store.Load())
            Roots.Add(DbTreeNode.Server(profile));
        _ = RestoreLastDatabasesAsync();
    }

    /// <summary>
    /// On startup, for each connection that has a remembered last database,
    /// expand the server node and pre-select that database in the tree.
    /// </summary>
    private async Task RestoreLastDatabasesAsync()
    {
        var saved = SettingsStore.Current.LastDatabases;
        if (saved.Count == 0) return;

        foreach (var root in Roots.ToList())
        {
            if (!saved.TryGetValue(root.Connection.Id.ToString(), out var dbName)) continue;
            if (string.IsNullOrEmpty(dbName)) continue;

            // Load server children (databases / schemas)
            // Load server children if only the placeholder (NodeType.Message) is present
            if (root.Children.All(c => c.Type == NodeType.Message))
                await root.LoadChildrenAsync();

            var dbNode = root.Children.FirstOrDefault(c =>
                c.Type == NodeType.Database && c.Name == dbName);
            if (dbNode is null) continue;

            root.IsExpanded = true;
            dbNode.IsExpanded = true;
            SelectedNode = dbNode;
        }
    }

    private void Persist() =>
        _store.Save(Roots.Where(r => r.Type == NodeType.Server).Select(r => r.Connection));

    partial void OnTreeFilterChanged(string value)
    {
        DbTreeNode.ActiveFilter = value;
        foreach (var root in Roots) root.ApplyFilter(value);
    }

    // ---- connection management ------------------------------------------

    [RelayCommand]
    private void OpenQuery()
    {
        var node = SelectedNode;
        var connection = node?.Connection
            ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null)
        {
            StatusText = "Add a connection first.";
            return;
        }
        if (connection.Engine == DatabaseEngine.MongoDb)
        {
            Dialogs.ShowMessage("Not available",
                "The SQL query window doesn't apply to MongoDB.");
            return;
        }
        new Views.QueryWindow(connection, node?.Database).Show();
        StatusText = $"Opened a query window for '{connection.Name}'.";
    }

    [RelayCommand]
    private void OpenQueryBuilder()
    {
        var node = SelectedNode;
        var connection = node?.Connection
            ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null)
        {
            StatusText = "Add a connection first.";
            return;
        }
        if (connection.Engine == DatabaseEngine.MongoDb)
        {
            Dialogs.ShowMessage("Not available",
                "The visual query designer doesn't apply to MongoDB.");
            return;
        }
        if (connection.Engine == DatabaseEngine.Oracle)
        {
            Dialogs.ShowMessage("Not available",
                "The visual query designer isn't available for Oracle yet — use the SQL query window.");
            return;
        }
        new Views.QueryBuilderWindow(connection, node?.Database).Show();
        StatusText = $"Opened the query builder for '{connection.Name}'.";
    }

    [RelayCommand]
    private void OpenSchemaDiff()
    {
        var node = SelectedNode;
        var connection = node?.Connection
            ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null) { StatusText = "Add a connection first."; return; }
        if (connection.Engine is DatabaseEngine.MongoDb or DatabaseEngine.Sqlite)
        { Dialogs.ShowMessage("Not available", "Schema diff requires a server with multiple databases."); return; }
        new Views.SchemaDiffWindow(connection, node?.Database).Show();
    }

    [RelayCommand]
    private void OpenErDiagram()
    {
        var node = SelectedNode;
        var connection = node?.Connection
            ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null) { StatusText = "Add a connection first."; return; }
        if (connection.Engine == DatabaseEngine.MongoDb)
        { Dialogs.ShowMessage("Not available", "ER Diagram doesn't apply to MongoDB."); return; }
        var db = node?.Database;
        new Views.ErDiagramWindow(connection, db).Show();
    }

    [RelayCommand]
    private void OpenUserManager()
    {
        var node = SelectedNode;
        var connection = node?.Connection
            ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null)
        {
            StatusText = "Add a connection first.";
            return;
        }
        if (!Services.Security.SecurityProvider.IsSupported(connection.Engine))
        {
            Dialogs.ShowMessage("Not available",
                $"User & role management isn't available for {connection.Engine.DisplayName()}.");
            return;
        }
        new Views.UserManagerWindow(connection).Show();
        StatusText = $"Opened the user manager for '{connection.Name}'.";
    }

    [RelayCommand]
    private void NewUserShortcut()
    {
        OpenUserManager();
        if (System.Windows.Application.Current?.Windows.OfType<Views.UserManagerWindow>().LastOrDefault() is { } w
            && w.DataContext is UserManagerViewModel vm && vm.NewUserCommand.CanExecute(null))
            vm.NewUserCommand.Execute(null);
    }

    [RelayCommand]
    private void NewRoleShortcut()
    {
        OpenUserManager();
        if (System.Windows.Application.Current?.Windows.OfType<Views.UserManagerWindow>().LastOrDefault() is { } w
            && w.DataContext is UserManagerViewModel vm && vm.NewRoleCommand.CanExecute(null))
            vm.NewRoleCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (new Views.SettingsDialog().ShowDialog() == true)
            StatusText = "Settings saved (applies to tables opened from now on).";
    }

    private const string RepoUrl = "https://github.com/robertorenz/DataPortStudio";

    [RelayCommand]
    private static void ExitApp() => System.Windows.Application.Current?.Shutdown();

    [RelayCommand]
    private void ShowAbout()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var version = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        Dialogs.ShowMessage("About DataPortStudio",
            $"DataPortStudio — SQL Server Manager\n{version}\n\nA Navicat-style database manager for SQL Server.\n{RepoUrl}");
    }

    [RelayCommand]
    private void OpenGitHub() => OpenUrl(RepoUrl);

    [RelayCommand]
    private void OpenReleases() => OpenUrl(RepoUrl + "/releases/latest");

    [RelayCommand]
    private void OpenDocs()
    {
        try
        {
            new Views.HelpWindow { Owner = System.Windows.Application.Current?.MainWindow }.Show();
        }
        catch
        {
            // If the in-app help window can't open (e.g. WebView2 runtime missing), fall back to the online README.
            OpenUrl(RepoUrl + "#readme");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no browser available */ }
    }

    [RelayCommand]
    private void AddConnection()
    {
        var profile = new ConnectionProfile();
        if (Dialogs.EditConnection(profile))
        {
            Roots.Add(DbTreeNode.Server(profile));
            Persist();
            StatusText = $"Added connection '{profile.Name}'.";
        }
    }

    [RelayCommand]
    private void EditConnection(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Server }) return;

        var edited = node.Connection.Clone();
        if (Dialogs.EditConnection(edited))
        {
            // Copy edited values back into the live profile and rebuild the node.
            var idx = Roots.IndexOf(node);
            CopyInto(edited, node.Connection);
            Roots[idx] = DbTreeNode.Server(node.Connection);
            Persist();
            StatusText = $"Updated connection '{node.Connection.Name}'.";
        }
    }

    [RelayCommand]
    private void RemoveConnection(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Server }) return;

        if (Dialogs.Confirm("Remove connection",
                $"Remove the connection '{node.Connection.Name}'?"))
        {
            Roots.Remove(node);
            Persist();
            StatusText = "Connection removed.";
        }
    }

    [RelayCommand]
    private async Task RefreshNode(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null || node.IsLeaf) return;
        node.Reset();
        node.IsExpanded = true;
        await node.LoadChildrenAsync();
        StatusText = $"Refreshed '{node.Name}'.";
    }

    // ---- tabbed data viewing / editing ----------------------------------

    [RelayCommand]
    private async Task OpenTable(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null || !node.IsOpenable) return;

        // Already open? Just switch to it.
        var key = TableTabViewModel.MakeKey(node);
        var existing = Tabs.OfType<TableTabViewModel>().FirstOrDefault(t => t.Key == key);
        if (existing is not null)
        {
            ActiveTab = existing;
            StatusText = $"Switched to {existing.Identifier}.";
            return;
        }

        var tab = new TableTabViewModel(node, SettingsStore.Current.DefaultRowLimit,
            s => StatusText = s, b => IsBusy = b);
        tab.CloseRequested += CloseTab;
        Tabs.Add(tab);
        OnPropertyChanged(nameof(ShowEmpty));
        ActiveTab = tab;

        if (!await tab.LoadAsync())
            CloseTab(tab); // load failed — don't leave an empty tab behind
    }

    private static string RoutineKind(NodeType type) => type switch
    {
        NodeType.Procedure => "Procedure",
        NodeType.View => "View",
        _ => "Function"
    };

    private static string RoutineKeyword(NodeType type) => type switch
    {
        NodeType.Procedure => "PROCEDURE",
        NodeType.View => "VIEW",
        _ => "FUNCTION"
    };

    [RelayCommand]
    private void DesignTable(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Table }) return;
        new Views.TableDesignerWindow(node.Connection, node.Database, node.Schema!, node.Name, isNew: false).Show();
        StatusText = $"Designing {node.Schema}.{node.Name}.";
    }

    // ---- copy / paste a table -------------------------------------------

    private sealed record CopiedTable(ConnectionProfile Connection, string? Database, string? Schema, string Name);

    private CopiedTable? _copied;

    public bool HasCopiedTable => _copied is not null;

    [RelayCommand]
    private void CopyTable(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Table }) return;
        _copied = new CopiedTable(node.Connection, node.Database, node.Schema, node.Name);
        StatusText = $"Copied '{node.Name}'. Paste onto a Tables folder or table in the same database.";
    }

    [RelayCommand]
    private async Task PasteTable(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null) return;
        if (_copied is null) { StatusText = "Nothing to paste — copy a table first."; return; }

        // Resolve the paste target from the node.
        var conn = node.Connection;
        var db = node.Database ?? _copied.Database;
        // The target schema must belong to the TARGET. Fall back to the source's schema, but never
        // carry an empty schema into SQL Server (it would generate an invalid "[].[table]" identifier);
        // default to dbo there.
        var schema = !string.IsNullOrEmpty(node.Schema) ? node.Schema
            : !string.IsNullOrEmpty(_copied.Schema) ? _copied.Schema
            : conn.Engine == DatabaseEngine.SqlServer ? "dbo"
            : _copied.Schema;

        if (!TableCopyService.CanCopyBetween(_copied.Connection.Engine, conn.Engine))
        {
            Dialogs.ShowError("Paste table",
                $"Can't copy a {_copied.Connection.Engine.DisplayName()} table into {conn.Engine.DisplayName()}.");
            return;
        }

        var sameConn = conn.Id == _copied.Connection.Id;
        var sameDb = string.Equals(db, _copied.Database, StringComparison.OrdinalIgnoreCase);
        var inPlace = sameConn && sameDb;

        try
        {
            var existing = await TableCopyService.ListObjectsAsync(conn, db ?? "", schema ?? "");
            var newName = TableCopyService.FreeName(_copied.Name, existing);

            var mode = Dialogs.ChooseCopyMode(_copied.Name, newName);
            if (mode == Dialogs.CopyMode.Cancel) return;
            var withData = mode == Dialogs.CopyMode.StructureAndData;

            // For a Clarion file (.tps/.dat) → SQL copy, let the user review/tweak the target column types.
            IReadOnlyList<TableCopyService.ClarionColumnMap>? mappings = null;
            if (_copied.Connection.Engine.IsClarionFile() && !inPlace)
            {
                var proposed = await TableCopyService.ProposeClarionMappingAsync(_copied.Connection, _copied.Name, conn.Engine);
                var dlg = new Views.ColumnMappingDialog(_copied.Name, $"{conn.Name} ({conn.Engine.DisplayName()})", conn.Engine, proposed)
                    { Owner = System.Windows.Application.Current?.MainWindow };
                if (dlg.ShowDialog() != true) { StatusText = "Copy canceled."; return; }
                mappings = dlg.Result;
            }

            IsBusy = true;
            var where = inPlace ? "" : $" into '{conn.Name}'";
            StatusText = $"Copying '{_copied.Name}' → '{newName}'{where}…";

            if (inPlace)
                await TableCopyService.CopyAsync(conn,
                    _copied.Database ?? "", _copied.Schema ?? "", _copied.Name,
                    db ?? "", schema ?? "", newName, withData);
            else
                await TableCopyService.CopyCrossAsync(
                    _copied.Connection, _copied.Database ?? "", _copied.Schema ?? "", _copied.Name,
                    conn, db ?? "", schema ?? "", newName, withData, mappings);

            // Refresh both views so the new table shows up: the tree folder that now contains the
            // copy, and the Objects list (in case it's showing that folder).
            var folder = node.Type == NodeType.Category ? node
                : node.Type == NodeType.Table ? node.Parent
                : node;
            if (folder is not null) await RefreshNode(folder);
            if (_objectsTab is not null) await _objectsTab.LoadAsync();

            StatusText = $"Created '{newName}'" +
                         (mode == Dialogs.CopyMode.StructureAndData ? " with data." : " (structure only).");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Copy table failed", ex.Message);
            StatusText = "Copy failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GenerateInserts(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Table or NodeType.View }) return;
        StatusText = $"Generating INSERT script for {node.Schema}.{node.Name}…";
        try
        {
            var script = await ScriptService.GenerateInsertsAsync(
                node.Connection.BuildConnectionString(), node.Database ?? "", node.Schema!, node.Name,
                SettingsStore.Current.DefaultRowLimit);
            new Views.ScriptViewerWindow($"INSERT script — {node.Schema}.{node.Name}", script, $"{node.Name}_inserts").Show();
            StatusText = $"Generated INSERT script for {node.Schema}.{node.Name}.";
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Generate INSERT failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportData(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Table }) return;
        new Views.ImportDialog(node.Connection, node.Database ?? "", node.Schema!, node.Name).ShowDialog();
    }

    [RelayCommand]
    private void NewTable(DbTreeNode? category)
    {
        category ??= SelectedNode;
        // Accept the Tables category or a schema node.
        var schema = category?.Schema ?? "dbo";
        var connection = category?.Connection ?? Roots.FirstOrDefault(r => r.Type == NodeType.Server)?.Connection;
        if (connection is null) { StatusText = "Add a connection first."; return; }
        new Views.TableDesignerWindow(connection, category?.Database, schema, "NewTable", isNew: true).Show();
    }

    private static async Task<bool> ConfirmDrop(DbTreeNode node, string keyword, bool dataLoss = false)
    {
        var dependents = new List<string>();
        // The dependency check is SQL Server-specific. Running it for other engines would open a
        // SqlConnection with a non-SQL-Server connection string and block until it times out
        // (a long, pointless delay before the confirmation appears).
        if (node.Connection.Engine == DatabaseEngine.SqlServer)
        {
            try
            {
                dependents = await SqlServerService.GetDependentsAsync(
                    node.Connection.BuildConnectionString(), node.Database ?? "", node.Schema!, node.Name);
            }
            catch { /* dependency check is best-effort */ }
        }

        string L(string key) => LocalizationManager.Instance[key];
        var typeWord = L("ObjType_" + keyword.ToLowerInvariant()); // e.g. ObjType_table
        var title = string.Format(L("Drop_TitleFmt"), typeWord);
        var display = node.Connection.Engine == DatabaseEngine.SqlServer
            ? $"{node.Schema}.{node.Name}" : node.Name;

        var msg = string.Format(L("Drop_Q"), typeWord, display);
        if (dataLoss)
            msg += "\n\n" + L("Drop_DataLoss");
        if (dependents.Count > 0)
            msg += "\n\n" + L("Drop_Refs") + "\n• " + string.Join("\n• ", dependents.Take(15)) +
                   (dependents.Count > 15 ? "\n" + string.Format(L("Drop_AndMore"), dependents.Count - 15) : "");
        msg += "\n\n" + L("Drop_CannotUndo");

        return Dialogs.ConfirmDanger(title, msg, title);
    }

    [RelayCommand]
    private async Task DropTable(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Table }) return;
        if (!await ConfirmDrop(node, "TABLE", dataLoss: true))
            return;
        try
        {
            await ExecuteDropAsync(node, "TABLE");
            StatusText = $"Dropped table {node.Schema}.{node.Name}.";
            if (node.Parent is not null) await RefreshNode(node.Parent);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Drop failed", ex.Message);
        }
    }

    [RelayCommand]
    private void EditRoutine(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Function or NodeType.Procedure or NodeType.View }) return;
        var kind = RoutineKind(node.Type);
        new Views.RoutineEditorWindow(node.Connection, node.Database, node.Schema!, node.Name, kind).Show();
        StatusText = $"Editing {kind.ToLowerInvariant()} {node.Schema}.{node.Name}.";
    }

    /// <summary>Opens the routine editor with a template (node is a Functions/Procedures/Views category).</summary>
    [RelayCommand]
    private void NewRoutine(DbTreeNode? category)
    {
        category ??= SelectedNode;
        if (category is not { Type: NodeType.Category } c) return;
        var schema = c.Schema ?? "dbo";
        var (name, kind, template) = c.CategoryChildType switch
        {
            NodeType.Procedure => ("NewProcedure", "Procedure",
                $"CREATE PROCEDURE [{schema}].[NewProcedure]\n    @Param1 int = 0\nAS\nBEGIN\n    SET NOCOUNT ON;\n    SELECT @Param1 AS Result;\nEND"),
            NodeType.View => ("NewView", "View",
                $"CREATE VIEW [{schema}].[NewView]\nAS\nSELECT 1 AS Col1"),
            _ => ("NewFunction", "Function",
                $"CREATE FUNCTION [{schema}].[NewFunction] (@Param1 int)\nRETURNS int\nAS\nBEGIN\n    RETURN @Param1;\nEND")
        };
        new Views.RoutineEditorWindow(c.Connection, c.Database, schema, name, kind, template).Show();
    }

    [RelayCommand]
    private void ExecuteRoutine(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Function or NodeType.Procedure }) return;
        var qualified = $"[{node.Schema}].[{node.Name}]";
        var sql = node.Type == NodeType.Procedure
            ? $"EXEC {qualified} "
            : $"-- Scalar function: SELECT {qualified}(/* args */)\n-- Table function: SELECT * FROM {qualified}(/* args */)\nSELECT {qualified}()";
        new Views.QueryWindow(node.Connection, node.Database, sql).Show();
    }

    [RelayCommand]
    private async Task DropRoutine(DbTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is not { Type: NodeType.Function or NodeType.Procedure or NodeType.View }) return;
        var keyword = RoutineKeyword(node.Type);
        if (!await ConfirmDrop(node, keyword))
            return;
        try
        {
            await ExecuteDropAsync(node, keyword);
            StatusText = $"Dropped {keyword.ToLowerInvariant()} {node.Schema}.{node.Name}.";
            if (node.Parent is not null) await RefreshNode(node.Parent);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Drop failed", ex.Message);
        }
    }

    /// <summary>Engine-aware DROP of a table/view/function/procedure node.</summary>
    private static Task ExecuteDropAsync(DbTreeNode node, string keyword)
    {
        var cs = node.Connection.BuildConnectionString();
        var db = node.Database ?? "";
        return node.Connection.Engine switch
        {
            DatabaseEngine.Sqlite =>
                SqliteService.ExecuteWithoutForeignKeysAsync(cs, $"DROP {keyword} \"{node.Name}\""),
            DatabaseEngine.Firebird =>
                FirebirdService.ExecuteAsync(cs, $"DROP {keyword} \"{node.Name}\""),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                MySqlService.ExecuteAsync(cs, db, $"DROP {keyword} `{node.Name}`"),
            DatabaseEngine.Oracle =>
                OracleService.ExecuteAsync(cs, $"DROP {keyword} \"{node.Name}\""),
            _ => SqlServerService.ExecuteAsync(cs, db, $"DROP {keyword} [{node.Schema}].[{node.Name}]")
        };
    }

    private void CloseTab(TableTabViewModel tab)
    {
        if (tab.HasUnsavedChangesNow &&
            !Dialogs.Confirm("Close tab",
                $"'{tab.Identifier}' has unsaved changes. Close anyway?"))
            return;

        tab.CloseRequested -= CloseTab;
        var index = Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(ActiveTab, tab);
        Tabs.Remove(tab);
        tab.Dispose();
        OnPropertyChanged(nameof(ShowEmpty));

        if (wasActive)
            ActiveTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
    }

    private static void CopyInto(ConnectionProfile from, ConnectionProfile to)
    {
        to.Name = from.Name;
        to.Engine = from.Engine;
        to.Server = from.Server;
        to.FilePath = from.FilePath;
        to.Port = from.Port;
        to.FirebirdEmbedded = from.FirebirdEmbedded;
        to.Database = from.Database;
        to.IntegratedSecurity = from.IntegratedSecurity;
        to.Username = from.Username;
        to.Password = from.Password;
        to.Encrypt = from.Encrypt;
        to.TrustServerCertificate = from.TrustServerCertificate;
        to.UseRawConnectionString = from.UseRawConnectionString;
        to.RawConnectionString = from.RawConnectionString;
    }
}
