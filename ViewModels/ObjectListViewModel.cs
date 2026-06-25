using System.Collections.ObjectModel;
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
    private readonly Action<DbTreeNode, ObjectListItem> _delete;
    private readonly Action<DbTreeNode> _new;
    private readonly Action<DbTreeNode, ObjectListItem> _copy;
    private readonly Action<DbTreeNode> _paste;

    private DbTreeNode? _container;

    public ObservableCollection<ObjectListItem> Items { get; } = new();

    [ObservableProperty] private ObjectListItem? selectedItem;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private bool canDesign;
    [ObservableProperty] private bool canCreate;
    [ObservableProperty] private bool canDelete = true;
    [ObservableProperty] private bool canPaste = true;
    [ObservableProperty] private bool isTables = true;

    /// <summary>The kind of objects listed: Table, View, Function, or Procedure.</summary>
    public NodeType ChildType { get; private set; } = NodeType.Table;

    public string Header => LocalizationManager.Instance["Tab_Objects"];
    public bool CanClose => false;
    public string TabToolTip => Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(TabToolTip));

    public ObjectListViewModel(
        Action<DbTreeNode, ObjectListItem> open, Action<DbTreeNode, ObjectListItem> design,
        Action<DbTreeNode, ObjectListItem> delete, Action<DbTreeNode> @new,
        Action<DbTreeNode, ObjectListItem> copy, Action<DbTreeNode> paste)
    {
        _open = open;
        _design = design;
        _delete = delete;
        _new = @new;
        _copy = copy;
        _paste = paste;
    }

    /// <summary>Points the Objects tab at a new container and reloads it.</summary>
    public async Task ConfigureAsync(DbTreeNode container)
    {
        _container = container;
        ChildType = container.Type == NodeType.Category ? container.CategoryChildType : NodeType.Table;
        IsTables = ChildType == NodeType.Table;
        var engine = container.Connection.Engine;
        CanDesign = IsTables && engine is DatabaseEngine.SqlServer or DatabaseEngine.Sqlite;
        CanCreate = CanDesign;
        // Read-only engines (MongoDB, Clarion TPS/DAT) can't be dropped or pasted into — hide those.
        // Copy stays available so their data can be copied out to a SQL database.
        CanDelete = !engine.IsReadOnly();
        CanPaste = IsTables && !engine.IsReadOnly();

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
        IsLoading = true;
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
        }
        catch (Exception ex)
        {
            CountText = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
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
    private async Task Refresh() => await LoadAsync();
}
