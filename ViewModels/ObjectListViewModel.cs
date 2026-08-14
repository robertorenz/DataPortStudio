using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.ViewModels;

/// <summary>The persistent "Objects" tab — lists the tables/collections of the selected container.</summary>
public partial class ObjectListViewModel : ObservableObject, ITabItem
{
    private readonly Action<DbTreeNode, ObjectListItem> _open;
    private readonly Action<DbTreeNode, ObjectListItem> _design;
    private readonly Action<DbTreeNode, ObjectListItem> _rename;
    private readonly Action<DbTreeNode, ObjectListItem> _delete;
    private readonly Action<DbTreeNode> _new;
    private readonly Action<DbTreeNode, ObjectListItem> _copy;
    private readonly Action<DbTreeNode> _paste;
    private readonly Action<DbTreeNode, ObjectListItem> _generateObjectScript;
    private readonly Action<DbTreeNode, ObjectListItem> _generateInserts;

    private DbTreeNode? _container;

    /// <summary>Cancels the background detail pass when a different container is selected.</summary>
    private CancellationTokenSource? _detailCts;

    public ObservableCollection<ObjectListItem> Items { get; } = new();
    /// <summary>The name-filtered view bound to the Objects grid.</summary>
    public ICollectionView FilteredItems { get; }

    [ObservableProperty] private ObjectListItem? selectedItem;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private bool canDesign;
    [ObservableProperty] private bool canCreate;
    [ObservableProperty] private bool canRename;
    [ObservableProperty] private bool canDelete = true;
    [ObservableProperty] private bool canPaste = true;
    [ObservableProperty] private bool isTables = true;
    [ObservableProperty] private bool canGenerateObjectScript;
    [ObservableProperty] private bool canGenerateInserts;
    [ObservableProperty] private string locatorText = "";
    [ObservableProperty] private string locatorStatus = "";
    [ObservableProperty] private string searchPlaceholder = "";
    [ObservableProperty] private string searchToolTip = "";

    public bool IsFilterMode => SettingsStore.Current.ObjectSearchMode == ObjectSearchMode.Filter;

    /// <summary>The kind of objects listed: Table, View, Function, or Procedure.</summary>
    public NodeType ChildType { get; private set; } = NodeType.Table;

    public string Header => LocalizationManager.Instance["Tab_Objects"];
    public bool CanClose => false;
    public string TabToolTip => Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(TabToolTip));
    partial void OnLocatorTextChanged(string value)
    {
        if (IsFilterMode) FilteredItems.Refresh();
        Locate(reset: true);
    }

    public ObjectListViewModel(
        Action<DbTreeNode, ObjectListItem> open, Action<DbTreeNode, ObjectListItem> design,
        Action<DbTreeNode, ObjectListItem> rename, Action<DbTreeNode, ObjectListItem> delete,
        Action<DbTreeNode> @new, Action<DbTreeNode, ObjectListItem> copy, Action<DbTreeNode> paste,
        Action<DbTreeNode, ObjectListItem> generateObjectScript,
        Action<DbTreeNode, ObjectListItem> generateInserts)
    {
        _open = open;
        _design = design;
        _rename = rename;
        _delete = delete;
        _new = @new;
        _copy = copy;
        _paste = paste;
        _generateObjectScript = generateObjectScript;
        _generateInserts = generateInserts;
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = MatchesLocator;
        ApplySearchMode();
    }

    /// <summary>Points the Objects tab at a new container and reloads it.</summary>
    public async Task ConfigureAsync(DbTreeNode container)
    {
        _container = container;
        LocatorText = "";
        LocatorStatus = "";
        ChildType = container.Type == NodeType.Category ? container.CategoryChildType : NodeType.Table;
        IsTables = ChildType == NodeType.Table;
        var engine = container.Connection.Engine;
        CanDesign = IsTables && engine is DatabaseEngine.SqlServer or DatabaseEngine.Sqlite;
        CanCreate = CanDesign;
        // Read-only engines and file-folder engines (MongoDB, Clarion, Excel) can't be dropped or pasted into.
        // Copy stays available so their data can be copied out to a SQL database.
        var isFolderEngine = engine is DatabaseEngine.Tps or DatabaseEngine.ClarionDat or DatabaseEngine.Excel;
        CanRename = IsTables && engine is (DatabaseEngine.SqlServer or DatabaseEngine.Sqlite
                    or DatabaseEngine.MySql or DatabaseEngine.MariaDb or DatabaseEngine.Oracle);
        CanDelete = !engine.IsReadOnly() && !isFolderEngine;
        CanPaste = IsTables && !engine.IsReadOnly() && !isFolderEngine;
        CanGenerateObjectScript = TableCopyService.IsRelational(engine) &&
            ChildType is NodeType.Table or NodeType.View or NodeType.Function or NodeType.Procedure;
        CanGenerateInserts = engine == DatabaseEngine.SqlServer &&
            ChildType is NodeType.Table or NodeType.View;

        var loc = LocalizationManager.Instance;
        var kindWord = ChildType switch
        {
            NodeType.View => loc["OL_Views"],
            NodeType.Function => loc["OL_Functions"],
            NodeType.Procedure => loc["OL_Procedures"],
            _ => engine == DatabaseEngine.MongoDb ? loc["OL_Collections"] : loc["OL_Tables"]
        };
        var where = container.Type is NodeType.Database or NodeType.Server
            ? container.Name
            : $"{container.Database}.{(container.Type == NodeType.Schema ? container.Name : container.Schema)}";
        Title = $"{where} — {kindWord}";
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_container is null) return;

        // Abandon any detail pass still running for the previously selected container.
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        IsLoading = true;
        var loaded = false;
        try
        {
            var db = _container.Database ?? _container.Name;
            var schema = _container.Schema ?? "";
            var items = ChildType == NodeType.Table
                ? await ObjectListService.LoadTablesAsync(_container.Connection, db, schema)
                : await ObjectListService.LoadNamesAsync(_container.Connection, db, schema,
                    ChildType switch { NodeType.View => "view", NodeType.Function => "function", _ => "procedure" });
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            CountText = string.Format(LocalizationManager.Instance["OL_Count"], Items.Count);
            FilteredItems.Refresh();
            if (!string.IsNullOrWhiteSpace(LocatorText)) Locate(reset: true);
            loaded = true;
        }
        catch (Exception ex)
        {
            CountText = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        // Deliberately not awaited: the list is already usable, and the remaining details trickle
        // in behind it rather than holding up whoever is waiting on the load.
        if (loaded)
        {
            var cts = new CancellationTokenSource();
            _detailCts = cts;
            _ = FillFolderDetailsAsync(cts.Token);
        }
    }

    /// <summary>
    /// Fills in the details that cost a file open — an Excel workbook's sheet count — one file at a
    /// time, after the rows are already on screen. Awaiting each file hands the UI thread back
    /// between them, so a folder of large workbooks fills in progressively instead of freezing the
    /// tab while every workbook is opened up front.
    /// </summary>
    private async Task FillFolderDetailsAsync(CancellationToken token)
    {
        if (_container is null || _container.Connection.Engine != DatabaseEngine.Excel) return;

        var profile = _container.Connection;
        var pending = Items.Where(i => ObjectListService.NeedsFolderDescription(i.Name)).ToList();
        if (pending.Count == 0) return;

        var loc = LocalizationManager.Instance;
        var baseCount = CountText;
        var done = 0;

        try
        {
            foreach (var item in pending)
            {
                token.ThrowIfCancellationRequested();
                var detail = await Task.Run(
                    () => ObjectListService.DescribeFolderFile(profile, item.Name), token);
                token.ThrowIfCancellationRequested();

                if (detail is not null) item.Comment = detail;
                done++;
                CountText = $"{baseCount} — {string.Format(loc["OL_ReadingFiles"], done, pending.Count)}";
            }
            CountText = baseCount;
        }
        catch (OperationCanceledException)
        {
            // A different container was selected; its own load owns the status line now.
        }
        catch
        {
            CountText = baseCount;
        }
    }

    [RelayCommand]
    private void Open()
    {
        if (_container is not null && SelectedItem is not null) _open(_container, SelectedItem);
    }

    [RelayCommand]
    private void Design()
    {
        if (_container is not null && SelectedItem is not null) _design(_container, SelectedItem);
    }

    [RelayCommand]
    private void Rename()
    {
        if (_container is not null && SelectedItem is not null) _rename(_container, SelectedItem);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_container is not null && SelectedItem is not null) _delete(_container, SelectedItem);
    }

    [RelayCommand]
    private void New()
    {
        if (_container is not null) _new(_container);
    }

    [RelayCommand]
    private void Copy()
    {
        if (_container is not null && SelectedItem is not null) _copy(_container, SelectedItem);
    }

    [RelayCommand]
    private void Paste()
    {
        if (_container is not null) _paste(_container);
    }

    [RelayCommand]
    private void GenerateObjectScript()
    {
        if (_container is not null && SelectedItem is not null)
            _generateObjectScript(_container, SelectedItem);
    }

    [RelayCommand]
    private void GenerateInserts()
    {
        if (_container is not null && SelectedItem is not null)
            _generateInserts(_container, SelectedItem);
    }

    [RelayCommand]
    private async Task Refresh() => await LoadAsync();

    [RelayCommand]
    private void LocateNext() => Locate(reset: false);

    [RelayCommand]
    private void ClearLocator()
    {
        LocatorText = "";
        LocatorStatus = "";
    }

    /// <summary>Reapplies the saved Locator/Filter behavior, including an already-entered query.</summary>
    public void ApplySearchMode()
    {
        OnPropertyChanged(nameof(IsFilterMode));
        SearchPlaceholder = LocalizationManager.Instance[
            IsFilterMode ? "OL_FilterPlaceholder" : "OL_LocatorPlaceholder"];
        SearchToolTip = LocalizationManager.Instance[
            IsFilterMode ? "OL_FilterTooltip" : "OL_LocatorTooltip"];
        FilteredItems.Refresh();
        Locate(reset: true);
    }

    /// <summary>Filters and selects matching objects by name only; schema and metadata are ignored.</summary>
    private void Locate(bool reset)
    {
        var query = LocatorText.Trim();
        if (query.Length == 0)
        {
            LocatorStatus = "";
            return;
        }

        var matches = Items
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            SelectedItem = null;
            LocatorStatus = IsFilterMode
                ? ""
                : LocalizationManager.Instance["OL_LocatorNoMatches"];
            return;
        }

        ObjectListItem match;
        if (reset)
        {
            // A prefix match feels most natural while typing; fall back to a match anywhere.
            match = matches.FirstOrDefault(item =>
                item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) ?? matches[0];
        }
        else
        {
            var current = SelectedItem is null ? -1 : matches.IndexOf(SelectedItem);
            match = matches[(current + 1) % matches.Count];
        }

        SelectedItem = match;
        LocatorStatus = IsFilterMode
            ? ""
            : string.Format(LocalizationManager.Instance["OL_LocatorPosition"],
                matches.IndexOf(match) + 1, matches.Count);
    }

    private bool MatchesLocator(object item) =>
        item is ObjectListItem obj &&
        (!IsFilterMode || string.IsNullOrWhiteSpace(LocatorText) ||
         obj.Name.Contains(LocatorText.Trim(), StringComparison.OrdinalIgnoreCase));
}
