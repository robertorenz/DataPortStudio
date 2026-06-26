using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;
using DataPortStudio.Views;

namespace DataPortStudio.ViewModels;

/// <summary>
/// One open table, shown as a tab. Owns its own editable session and data,
/// independent of any other open tab.
/// </summary>
public partial class TableTabViewModel : ObservableObject, IDisposable, ITabItem
{
    public bool CanClose => true;

    /// <summary>Hover tooltip: connection, engine, full location, and load/key info.</summary>
    public string TabToolTip
    {
        get
        {
            var loc = LocalizationManager.Instance;
            var c = Node.Connection;
            var location = c.Engine switch
            {
                DatabaseEngine.Sqlite or DatabaseEngine.Firebird
                    or DatabaseEngine.Tps or DatabaseEngine.ClarionDat or DatabaseEngine.Excel => Node.Name,
                DatabaseEngine.MongoDb or DatabaseEngine.MySql or DatabaseEngine.MariaDb => $"{Node.Database}.{Node.Name}",
                _ => $"{Node.Database}.{Node.Schema}.{Node.Name}"
            };
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{c.Name}  ({c.Engine.DisplayName()})");
            sb.Append(location);
            if (_session is not null)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format(loc["Tip_RowsLoaded"], _session.Data.Rows.Count, RowLimit));
                sb.Append(string.Format(loc["Tip_Key"], _session.KeyDescription));
            }
            else if (_sourceData is not null)
            {
                sb.AppendLine();
                sb.Append(string.Format(loc["Tip_DocsLoaded"], _sourceData.Rows.Count, RowLimit));
            }
            return sb.ToString();
        }
    }

    private EditableTableSession? _session;
    /// <summary>The grid's backing table — the editable session's data, or a read-only table (MongoDB).</summary>
    private DataTable? _sourceData;
    private readonly Action<string> _setStatus;
    private readonly Action<bool> _setBusy;

    // TPS edit session state (no EditableTableSession — TpsWriter handles write-back directly).
    private TpsParser.TableDefinition? _tpsDef;
    private int _tpsTableNumber;
    private string? _tpsPath;

    private bool _isExcel;

    public DbTreeNode Node { get; }
    /// <summary>Uniquely identifies the table so duplicate tabs aren't opened.</summary>
    public string Key { get; }
    /// <summary>Tab caption.</summary>
    public string Header { get; }
    /// <summary>Fully-qualified name shown in the tab's toolbar.</summary>
    public string Identifier { get; }

    [ObservableProperty] private DataView? gridData;
    [ObservableProperty] private bool hasUnsavedChanges;
    /// <summary>Read-only grid (e.g. MongoDB document viewer) — disables all editing.</summary>
    [ObservableProperty] private bool gridReadOnly;
    public bool GridEditable => !GridReadOnly;
    partial void OnGridReadOnlyChanged(bool value) => OnPropertyChanged(nameof(GridEditable));
    /// <summary>Allows adding and deleting rows. False for edit-only engines (TPS) that support UPDATE but not INSERT/DELETE.</summary>
    [ObservableProperty] private bool gridCanModifyRows = true;
    /// <summary>True when the toolbar is too narrow for labels — buttons collapse to icons.</summary>
    [ObservableProperty] private bool isToolbarCompact;
    [ObservableProperty] private int rowLimit;
    [ObservableProperty] private bool showClarionTypes = true;

    [ObservableProperty] private bool showDetailPanel;
    [ObservableProperty] private string? detailColumn;
    [ObservableProperty] private string detailText = "";
    [ObservableProperty] private string detailHex = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? detailImage;
    [ObservableProperty] private string detailHtml = "";
    [ObservableProperty] private bool viewMenuOpen;
    [ObservableProperty] private CellViewMode viewMode = CellViewMode.Auto;

    [ObservableProperty] private bool detailPopped;
    [ObservableProperty] private bool showSqlPanel;
    [ObservableProperty] private string sqlPreview = "";
    [ObservableProperty] private bool sqlPopped;

    /// <summary>Resizable heights of the docked panes (pixels), driven by their drag handles.</summary>
    [ObservableProperty] private double detailPaneHeight = 240;
    [ObservableProperty] private double sqlPaneHeight = 240;

    private bool _sqlRefreshQueued;

    public string PaneTitleSuffix => Identifier;

    private CellViewMode _effectiveViewMode = CellViewMode.Text;
    public bool IsTextMode => _effectiveViewMode == CellViewMode.Text;
    public bool IsHexMode => _effectiveViewMode == CellViewMode.Hex;
    public bool IsImageMode => _effectiveViewMode == CellViewMode.Image;
    public bool IsWebMode => _effectiveViewMode == CellViewMode.Web;
    public string ViewModeLabel => LocalizationManager.Instance["View_" + _effectiveViewMode];
    /// <summary>Apply is only meaningful for editable text on a string column.</summary>
    public bool CanApplyDetail => IsTextMode && _detailIsString;

    private DataRowView? _detailRow;
    private string? _detailColumnName;
    private byte[]? _detailBytes;
    private bool _detailIsString;

    private readonly RowIdentityStore _identityStore = new();
    private string? _identityKey;

    // ---- structure inspector ---------------------------------------------
    [ObservableProperty] private bool showInspector;
    [ObservableProperty] private bool inspectorPopped;
    [ObservableProperty] private double inspectorWidth = 400;
    [ObservableProperty] private string inspectorContent = "";
    [ObservableProperty] private InspectorSection inspectorSection = InspectorSection.Ddl;

    private TableStructure? _structure;

    /// <summary>Object name + type shown as a header above the structure inspector.</summary>
    public string StructureName => Node.Name;
    public bool IsStructureView => Node.Type == NodeType.View;
    public string StructureType =>
        LocalizationManager.Instance[IsStructureView ? "Hdr_View" : "Hdr_Table"];

    public bool IsInfoSection => InspectorSection == InspectorSection.Info;
    public bool IsDdlSection => InspectorSection == InspectorSection.Ddl;
    public bool IsIndexSection => InspectorSection == InspectorSection.Indexes;
    public string PaneTitle => Identifier;

    /// <summary>Shown only for tables without a primary key / unique index.</summary>
    public bool CanPickRowIdentity => _session is not null && !_session.HasNaturalKey;

    // ---- filter / sort ---------------------------------------------------
    public ObservableCollection<string> ColumnNames { get; } = new();
    public ObservableCollection<SortLevel> SortLevels { get; } = new();
    public ObservableCollection<FilterCondition> FilterConditions { get; } = new();
    [ObservableProperty] private bool filterMatchAll = true;
    [ObservableProperty] private bool hasActiveSort;
    [ObservableProperty] private bool hasActiveFilter;

    private string _sortExpression = "";
    private string _filterExpression = "";

    public Array SortDirections { get; } = Enum.GetValues(typeof(SortDirection));
    public Array FilterOperators { get; } = Enum.GetValues(typeof(FilterOperator));

    /// <summary>Columns detected as Clarion dates/times in the current data, by kind.</summary>
    public Dictionary<string, ClarionKind> ClarionColumns { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Manual per-column overrides set via the header right-click menu.
    /// A present key wins over detection; a null value forces "plain number".
    /// </summary>
    public Dictionary<string, ClarionKind?> ClarionOverrides { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasClarionTypes => ClarionColumns.Count > 0;

    /// <summary>Resolves how a column should be displayed: date, time, or plain (null).</summary>
    public ClarionKind? GetEffectiveKind(string column)
    {
        if (!ShowClarionTypes) return null;
        if (ClarionOverrides.TryGetValue(column, out var ov)) return ov;
        return ClarionColumns.TryGetValue(column, out var k) ? k : null;
    }

    public bool HasOverride(string column) => ClarionOverrides.ContainsKey(column);

    // The header right-click menu updates only the affected column's display in place (see
    // DataGridClarion), so these just record the override — no full re-projection that would
    // reset the grid's scroll position.
    public void SetClarionOverride(string column, ClarionKind? kind) => ClarionOverrides[column] = kind;

    public void ClearClarionOverride(string column) => ClarionOverrides.Remove(column);

    public string ClarionToggleLabel =>
        HasClarionTypes
            ? $"{LocalizationManager.Instance["Clarion_Fields"]} ({ClarionColumns.Count})"
            : LocalizationManager.Instance["Clarion_Fields"];

    public event Action<TableTabViewModel>? CloseRequested;

    partial void OnShowClarionTypesChanged(bool value) => RefreshView();

    /// <summary>Re-projects the same data (applying current filter+sort) so the grid refreshes.</summary>
    private void RefreshView()
    {
        ProjectView();
        _detailRow = null; // old view's row handle is stale after re-projection
    }

    /// <summary>Builds the bound DataView from the session, applying the current filter and sort.</summary>
    private void ProjectView()
    {
        if (_sourceData is null) return;
        var view = new DataView(_sourceData);
        if (!string.IsNullOrEmpty(_filterExpression))
        {
            try { view.RowFilter = _filterExpression; } catch { /* keep unfiltered */ }
        }
        if (!string.IsNullOrEmpty(_sortExpression))
        {
            try { view.Sort = _sortExpression; } catch { /* keep unsorted */ }
        }
        GridData = view;
    }

    // ---- cell detail panel ----------------------------------------------

    /// <summary>Called by the grid when the current cell changes.</summary>
    public void SetDetail(DataRowView? row, string? column, object? value)
    {
        _detailRow = row;
        _detailColumnName = column;
        DetailColumn = column;

        var actual = value is DBNull ? null : value;
        if (actual is byte[] bytes)
        {
            _detailBytes = bytes;
            _detailIsString = false;
            DetailText = $"(binary — {bytes.Length:N0} byte(s))";
        }
        else
        {
            var text = actual?.ToString() ?? "";
            DetailText = text;
            _detailBytes = System.Text.Encoding.UTF8.GetBytes(text);
            _detailIsString = actual is string || actual is null;
        }

        UpdateDerived();
    }

    [RelayCommand]
    private void SetViewMode(CellViewMode mode)
    {
        ViewMode = mode;
        ShowDetailPanel = true;
        ViewMenuOpen = false;
    }

    partial void OnViewModeChanged(CellViewMode value) => UpdateDerived();

    partial void OnDetailTextChanged(string value)
    {
        // Keep the Web view in sync while the user is in text/web on a string.
        if (IsWebMode) DetailHtml = value;
    }

    private CellViewMode ResolveMode()
    {
        if (ViewMode != CellViewMode.Auto) return ViewMode;
        if (CellContent.LooksLikeImage(_detailBytes)) return CellViewMode.Image;
        if (_detailIsString && CellContent.LooksLikeHtml(DetailText)) return CellViewMode.Web;
        if (!_detailIsString) return CellViewMode.Hex;
        return CellViewMode.Text;
    }

    private void UpdateDerived()
    {
        _effectiveViewMode = ResolveMode();

        DetailHex = _effectiveViewMode == CellViewMode.Hex ? CellContent.BuildHexDump(_detailBytes) : "";
        DetailImage = _effectiveViewMode == CellViewMode.Image ? CellContent.TryLoadImage(_detailBytes) : null;
        DetailHtml = _effectiveViewMode == CellViewMode.Web ? DetailText : "";

        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsHexMode));
        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(IsWebMode));
        OnPropertyChanged(nameof(ViewModeLabel));
        OnPropertyChanged(nameof(CanApplyDetail));
    }

    [RelayCommand]
    private void HideDetailPanel()
    {
        PaneService.ClosePopOut(this, PaneKind.Detail);
        ShowDetailPanel = false;
        ViewMenuOpen = false;
    }

    [RelayCommand]
    private void PinDetail() => PaneService.TogglePopOut(this, PaneKind.Detail);

    [RelayCommand]
    private void PinSql() => PaneService.TogglePopOut(this, PaneKind.Sql);

    // ---- structure inspector --------------------------------------------

    [RelayCommand]
    private void HideInspector()
    {
        PaneService.ClosePopOut(this, PaneKind.Inspector);
        ShowInspector = false;
    }

    [RelayCommand]
    private void PinInspector() => PaneService.TogglePopOut(this, PaneKind.Inspector);

    [RelayCommand]
    private void SetInspectorSection(InspectorSection section)
    {
        InspectorSection = section;
        ShowInspector = true;
    }

    partial void OnShowInspectorChanged(bool value)
    {
        if (value && _structure is null) _ = LoadStructureAsync();
    }

    partial void OnInspectorSectionChanged(InspectorSection value)
    {
        UpdateInspectorContent();
        OnPropertyChanged(nameof(IsInfoSection));
        OnPropertyChanged(nameof(IsDdlSection));
        OnPropertyChanged(nameof(IsIndexSection));
    }

    private async Task LoadStructureAsync()
    {
        InspectorContent = "Loading…";
        try
        {
            _structure = await TableMetadataService.GetAsync(
                Node.Connection.Engine, Node.Connection.BuildConnectionString(),
                Node.Database!, Node.Schema!, Node.Name, Node.Connection.Name);
            UpdateInspectorContent();
        }
        catch (Exception ex)
        {
            InspectorContent = "-- Error loading structure: " + ex.Message;
        }
    }

    /// <summary>Opens the panels the user has chosen to show by default (after the table loads).</summary>
    private void ApplyDefaults()
    {
        var s = SettingsStore.Current;
        ShowClarionTypes = s.ShowClarionTypesByDefault;
        if (s.ShowStructureByDefault)
        {
            InspectorSection = s.DefaultStructureSection;
            ShowInspector = true;
        }
        if (s.ShowSqlByDefault) ShowSqlPanel = true;
        if (s.ShowCellDetailByDefault) ShowDetailPanel = true;
    }

    private void UpdateInspectorContent()
    {
        if (_structure is null) return;
        InspectorContent = InspectorSection switch
        {
            InspectorSection.Info => _structure.Info,
            InspectorSection.Indexes => _structure.Indexes,
            _ => _structure.Ddl
        };
    }

    [RelayCommand]
    private void PickRowIdentity()
    {
        if (_session is null) return;
        var dialog = new RowIdentityDialog(_session);
        if (dialog.ShowDialog() != true) return;

        _session.SetRowIdentity(dialog.SelectedColumns);
        if (_identityKey is not null) _identityStore.Set(_identityKey, dialog.SelectedColumns);
        RefreshSqlPreview();
        _setStatus($"Row identity for {Identifier}: {_session.KeyDescription}.");
    }

    // ---- SQL preview pane ------------------------------------------------

    [RelayCommand]
    private void RefreshSqlPreview()
    {
        if (_session is null) { SqlPreview = ""; return; }
        try
        {
            var list = _session.BuildChangePreview();
            SqlPreview = list.Count == 0
                ? LocalizationManager.Instance["Sql_NoPending"]
                : string.Join(";\n\n", list) + ";";
        }
        catch (Exception ex)
        {
            SqlPreview = "-- Error generating preview: " + ex.Message;
        }
    }

    /// <summary>Refreshes the preview after edits settle, so a burst of changes only rebuilds once.</summary>
    private void QueueSqlRefresh()
    {
        if (!ShowSqlPanel || _sqlRefreshQueued) return;
        _sqlRefreshQueued = true;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _sqlRefreshQueued = false;
            RefreshSqlPreview();
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            _sqlRefreshQueued = false;
            if (ShowSqlPanel) RefreshSqlPreview();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    [RelayCommand]
    private void HideSqlPanel()
    {
        PaneService.ClosePopOut(this, PaneKind.Sql);
        ShowSqlPanel = false;
    }

    [RelayCommand]
    private async Task ExecuteSql()
    {
        await SaveChanges();
        RefreshSqlPreview();
    }

    partial void OnShowSqlPanelChanged(bool value)
    {
        if (value) RefreshSqlPreview();
    }

    [RelayCommand]
    private void ApplyDetail()
    {
        if (_detailRow is null || string.IsNullOrEmpty(_detailColumnName)) return;
        try
        {
            var table = _detailRow.Row.Table;
            if (!table.Columns.Contains(_detailColumnName)) return;
            var col = table.Columns[_detailColumnName]!;

            object newValue = col.DataType == typeof(string)
                ? DetailText
                : string.IsNullOrEmpty(DetailText)
                    ? DBNull.Value
                    : Convert.ChangeType(DetailText, col.DataType);

            _detailRow[_detailColumnName] = newValue;
            _setStatus($"Updated '{_detailColumnName}' for the selected row.");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not update cell", ex.Message);
        }
    }

    // ---- sort ------------------------------------------------------------

    [RelayCommand]
    private void AddSortLevel() =>
        SortLevels.Add(new SortLevel { Column = ColumnNames.FirstOrDefault() });

    [RelayCommand]
    private void RemoveSortLevel(SortLevel? level)
    {
        if (level is not null) SortLevels.Remove(level);
    }

    [RelayCommand]
    private void ApplySort()
    {
        var parts = SortLevels
            .Where(s => !string.IsNullOrEmpty(s.Column))
            .Select(s => $"{Bracket(s.Column!)} {(s.Direction == SortDirection.Desc ? "DESC" : "ASC")}");
        _sortExpression = string.Join(", ", parts);
        HasActiveSort = _sortExpression.Length > 0;
        ProjectView();
        _setStatus(HasActiveSort ? $"Sorted by {_sortExpression}." : "Sort cleared.");
    }

    [RelayCommand]
    private void ClearSort()
    {
        SortLevels.Clear();
        _sortExpression = "";
        HasActiveSort = false;
        ProjectView();
        _setStatus("Sort cleared.");
    }

    // ---- filter ----------------------------------------------------------

    [RelayCommand]
    private void AddFilterCondition() =>
        FilterConditions.Add(new FilterCondition { Column = ColumnNames.FirstOrDefault() });

    [RelayCommand]
    private void RemoveFilterCondition(FilterCondition? condition)
    {
        if (condition is not null) FilterConditions.Remove(condition);
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        var expr = BuildFilterExpression();
        try
        {
            // Validate against a throwaway view first so a bad expression doesn't blank the grid.
            var rows = new DataView(_sourceData!) { RowFilter = expr }.Count;
            _filterExpression = expr;
            HasActiveFilter = expr.Length > 0;
            ProjectView();
            _setStatus(HasActiveFilter ? $"Filter applied — {rows} row(s) match." : "Filter cleared.");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Invalid filter", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterConditions.Clear();
        _filterExpression = "";
        HasActiveFilter = false;
        ProjectView();
        _setStatus("Filter cleared.");
    }

    private string BuildFilterExpression()
    {
        var clauses = FilterConditions
            .Where(c => !string.IsNullOrEmpty(c.Column))
            .Select(BuildClause)
            .Where(c => c.Length > 0)
            .ToList();
        if (clauses.Count == 0) return "";
        var joiner = FilterMatchAll ? " AND " : " OR ";
        return string.Join(joiner, clauses.Select(c => $"({c})"));
    }

    private string BuildClause(FilterCondition c)
    {
        var col = Bracket(c.Column!);
        var isString = _sourceData is not null
            && _sourceData.Columns.Contains(c.Column!)
            && _sourceData.Columns[c.Column!]!.DataType == typeof(string);
        var raw = c.Value ?? "";

        string Literal(string v) => isString ? $"'{v.Replace("'", "''")}'" : v;
        string Like(string pattern) => $"{col} LIKE '{pattern.Replace("'", "''")}'";

        return c.Operator switch
        {
            FilterOperator.Contains => Like($"%{raw}%"),
            FilterOperator.StartsWith => Like($"{raw}%"),
            FilterOperator.EndsWith => Like($"%{raw}"),
            FilterOperator.Equals => $"{col} = {Literal(raw)}",
            FilterOperator.NotEquals => $"{col} <> {Literal(raw)}",
            FilterOperator.GreaterThan => $"{col} > {Literal(raw)}",
            FilterOperator.LessThan => $"{col} < {Literal(raw)}",
            FilterOperator.GreaterOrEqual => $"{col} >= {Literal(raw)}",
            FilterOperator.LessOrEqual => $"{col} <= {Literal(raw)}",
            FilterOperator.IsEmpty => isString ? $"{col} IS NULL OR {col} = ''" : $"{col} IS NULL",
            FilterOperator.IsNotEmpty => isString ? $"{col} IS NOT NULL AND {col} <> ''" : $"{col} IS NOT NULL",
            _ => ""
        };
    }

    private static string Bracket(string column) => "[" + column.Replace("]", "]]") + "]";

    public TableTabViewModel(DbTreeNode node, int rowLimit,
        Action<string> setStatus, Action<bool> setBusy)
    {
        Node = node;
        this.rowLimit = rowLimit;
        _setStatus = setStatus;
        _setBusy = setBusy;
        Key = MakeKey(node);
        Identifier = node.Connection.Engine switch
        {
            DatabaseEngine.Sqlite or DatabaseEngine.Firebird
                or DatabaseEngine.Tps or DatabaseEngine.ClarionDat
                or DatabaseEngine.Excel or DatabaseEngine.Oracle => node.Name,
            DatabaseEngine.MongoDb or DatabaseEngine.MySql or DatabaseEngine.MariaDb => $"{node.Database}.{node.Name}",
            _ => $"{node.Database}.{node.Schema}.{node.Name}"
        };
        Header = node.Name;
    }

    public static string MakeKey(DbTreeNode n) =>
        $"{n.Connection.Id}|{n.Database}|{n.Schema}|{n.Name}";

    public async Task<bool> LoadAsync()
    {
        _setBusy(true);
        _setStatus($"Loading {Identifier}…");
        try
        {
            Detach();

            if (Node.Connection.Engine == DatabaseEngine.MongoDb)
                return await LoadMongoAsync();

            if (Node.Connection.Engine == DatabaseEngine.Tps)
                return await LoadTpsAsync();

            if (Node.Connection.Engine == DatabaseEngine.ClarionDat)
                return await LoadClarionFileAsync(
                    () => DatService.ReadTable(Node.Connection.FilePath ?? "", Node.Name, RowLimit), "Clarion DAT file");

            if (Node.Connection.Engine == DatabaseEngine.Excel)
                return await LoadExcelAsync();

            _session = await EditableTableSession.OpenAsync(
                Node.Connection.Engine, Node.Connection.BuildConnectionString(),
                Node.Database!, Node.Schema!, Node.Name, RowLimit);
            _sourceData = _session.Data;

            _session.Data.RowChanged += OnDataChanged;
            _session.Data.RowDeleted += OnDataChanged;

            // Detect Clarion date/time columns before the grid generates its columns.
            ClarionColumns = ClarionDetector.Detect(_session.Data);
            OnPropertyChanged(nameof(HasClarionTypes));
            OnPropertyChanged(nameof(ClarionToggleLabel));

            ColumnNames.Clear();
            foreach (DataColumn c in _session.Data.Columns)
                ColumnNames.Add(c.ColumnName);

            // Apply a saved row-identity choice (keyless tables).
            _identityKey = RowIdentityStore.MakeKey(Node.Connection.Id, Node.Database, Node.Schema, Node.Name);
            if (!_session.HasNaturalKey)
            {
                var saved = _identityStore.Get(_identityKey);
                if (saved is not null) _session.SetRowIdentity(saved);
            }
            OnPropertyChanged(nameof(CanPickRowIdentity));

            ProjectView(); // applies any active filter/sort
            HasUnsavedChanges = false;
            ApplyDefaults();
            OnPropertyChanged(nameof(TabToolTip));

            var keyNote = _session.HasReliableKey
                ? ""
                : $"  ⚠ No primary key — edits/deletes match on {_session.KeyDescription} (one row at a time).";
            _setStatus($"Loaded {_session.Data.Rows.Count} row(s) from {Identifier} (limit {RowLimit}).{keyNote}");
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not open table", ex.Message);
            _setStatus("Failed to open table.");
            return false;
        }
        finally
        {
            _setBusy(false);
        }
    }

    /// <summary>Loads a TPS table into an editable grid. Cell edits can be saved back; INSERT/DELETE are unsupported.</summary>
    private async Task<bool> LoadTpsAsync()
    {
        var folder = Node.Connection.FilePath ?? "";
        try
        {
            _sourceData = await Task.Run(() =>
                TpsService.ReadTable(folder, Node.Name, RowLimit));

            (_tpsDef, _tpsTableNumber) = TpsService.GetTableDef(folder, Node.Name);
            _tpsPath = System.IO.Path.Combine(folder, Node.Name + ".tps");
            // Case-insensitive fallback: locate the actual file path.
            if (!System.IO.File.Exists(_tpsPath))
                _tpsPath = System.IO.Directory.EnumerateFiles(folder, "*.tps")
                    .FirstOrDefault(f => string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(f), Node.Name,
                        StringComparison.OrdinalIgnoreCase));

            GridReadOnly = false;
            GridCanModifyRows = false; // no INSERT / DELETE — UPDATE only

            _sourceData.RowChanged += OnDataChanged;
            _sourceData.RowDeleted += OnDataChanged;

            ClarionColumns = ClarionDetector.Detect(_sourceData);
            OnPropertyChanged(nameof(HasClarionTypes));
            OnPropertyChanged(nameof(ClarionToggleLabel));

            ColumnNames.Clear();
            foreach (DataColumn c in _sourceData.Columns)
                if (c.ColumnName != TpsService.RecordNumberColumn)
                    ColumnNames.Add(c.ColumnName);

            OnPropertyChanged(nameof(CanPickRowIdentity));
            ProjectView();
            HasUnsavedChanges = false;
            ApplyDefaults();
            OnPropertyChanged(nameof(TabToolTip));

            _setStatus($"Loaded {_sourceData.Rows.Count} record(s) from {Identifier} (limit {RowLimit}). " +
                       "TPS: cell edits only — no add/delete.");
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not open TPS file", ex.Message);
            _setStatus("Failed to open TPS file.");
            return false;
        }
        finally
        {
            _setBusy(false);
        }
    }

    /// <summary>Loads a MongoDB collection into a read-only grid (documents flattened to columns).</summary>
    private async Task<bool> LoadMongoAsync()
    {
        try
        {
            var uri = Node.Connection.BuildConnectionString();
            _sourceData = await MongoService.LoadCollectionAsync(uri, Node.Database!, Node.Name, RowLimit);

            GridReadOnly = true;
            ClarionColumns = new(StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(HasClarionTypes));
            OnPropertyChanged(nameof(ClarionToggleLabel));

            ColumnNames.Clear();
            foreach (DataColumn c in _sourceData.Columns)
                ColumnNames.Add(c.ColumnName);
            OnPropertyChanged(nameof(CanPickRowIdentity));

            ProjectView();
            HasUnsavedChanges = false;
            ApplyDefaults();
            OnPropertyChanged(nameof(TabToolTip));

            _setStatus($"Loaded {_sourceData.Rows.Count} document(s) from {Identifier} (limit {RowLimit}). " +
                       LocalizationManager.Instance["Mongo_ReadOnly"]);
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not open collection", ex.Message);
            _setStatus("Failed to open collection.");
            return false;
        }
        finally
        {
            _setBusy(false);
        }
    }

    /// <summary>Loads a Clarion flat file (.tps / .dat) into a read-only grid (records decoded to columns).</summary>
    private async Task<bool> LoadClarionFileAsync(Func<DataTable> read, string noun)
    {
        try
        {
            _sourceData = await Task.Run(read);

            GridReadOnly = true;

            // Decode any Clarion long/date/time columns the same way as SQL-sourced tables.
            ClarionColumns = ClarionDetector.Detect(_sourceData);
            OnPropertyChanged(nameof(HasClarionTypes));
            OnPropertyChanged(nameof(ClarionToggleLabel));

            ColumnNames.Clear();
            foreach (DataColumn c in _sourceData.Columns)
                ColumnNames.Add(c.ColumnName);
            OnPropertyChanged(nameof(CanPickRowIdentity));

            ProjectView();
            HasUnsavedChanges = false;
            ApplyDefaults();
            OnPropertyChanged(nameof(TabToolTip));

            _setStatus($"Loaded {_sourceData.Rows.Count} record(s) from {Identifier} (limit {RowLimit}). Read-only {noun}.");
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError($"Could not open {noun}", ex.Message);
            _setStatus($"Failed to open {noun}.");
            return false;
        }
        finally
        {
            _setBusy(false);
        }
    }

    /// <summary>Loads an Excel worksheet into an editable grid (full add/edit/delete support).</summary>
    private async Task<bool> LoadExcelAsync()
    {
        var folder = Node.Connection.FilePath ?? "";
        var fileName = Node.Database ?? "";
        var sheetName = Node.Schema ?? "";
        try
        {
            _sourceData = await Task.Run(() =>
                ExcelService.ReadTable(folder, fileName, sheetName, RowLimit));

            _isExcel = true;
            GridReadOnly = false;
            GridCanModifyRows = true;

            ColumnNames.Clear();
            foreach (DataColumn c in _sourceData.Columns)
                ColumnNames.Add(c.ColumnName);
            OnPropertyChanged(nameof(CanPickRowIdentity));

            _sourceData.RowChanged += OnDataChanged;
            _sourceData.RowDeleted += OnDataChanged;

            ProjectView();
            HasUnsavedChanges = false;
            ApplyDefaults();
            OnPropertyChanged(nameof(TabToolTip));

            _setStatus($"Loaded {_sourceData.Rows.Count} row(s) from {Identifier} (limit {RowLimit}).");
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Could not open Excel file", ex.Message);
            _setStatus("Failed to open Excel file.");
            return false;
        }
        finally
        {
            _setBusy(false);
        }
    }

    private async Task SaveExcelChangesAsync()
    {
        if (_sourceData is null) return;
        if (_sourceData.GetChanges() is null) return;

        _setBusy(true);
        try
        {
            var folder = Node.Connection.FilePath ?? "";
            var fileName = Node.Database ?? "";
            var sheetName = Node.Schema ?? "";
            var snapshot = _sourceData;

            await Task.Run(() => ExcelService.SaveTable(folder, fileName, sheetName, snapshot));
            _sourceData.AcceptChanges();
            HasUnsavedChanges = false;
            ProjectView();
            _setStatus($"Saved changes to {Identifier}.");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Save failed", ex.Message);
            _setStatus("Save failed.");
        }
        finally
        {
            _setBusy(false);
        }
    }

    [RelayCommand]
    private async Task Reload() => await LoadAsync();

    [RelayCommand]
    private void Export()
    {
        if (GridData is null) return;
        new ExportDialog(GridData, Identifier, DisplayOverride).ShowDialog();
    }

    /// <summary>Readable export value for Clarion date/time/timestamp columns; null = use raw.</summary>
    public string? DisplayOverride(string column, object? raw)
    {
        if (raw is null) return null;
        var kind = GetEffectiveKind(column);
        if (kind is null) return null;
        if (!TryLong(raw, out var n)) return null;
        return kind switch
        {
            ClarionKind.Date => ClarionDate.FromClarion(n)?.ToString("yyyy-MM-dd") ?? "",
            ClarionKind.Time => ClarionTime.Format(n) ?? "",
            ClarionKind.Timestamp => n <= 0 ? "" :
                DateTimeOffset.FromUnixTimeMilliseconds(n).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => null
        };
    }

    private static bool TryLong(object value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case decimal d: result = (long)d; return true;
            case double db: result = (long)db; return true;
            case float f: result = (long)f; return true;
            default: result = 0; return false;
        }
    }

    [RelayCommand]
    private async Task SaveChanges()
    {
        if (_tpsDef is not null)
        {
            await SaveTpsChangesAsync();
            return;
        }
        if (_isExcel)
        {
            await SaveExcelChangesAsync();
            return;
        }
        if (_session is null || !_session.HasChanges) return;
        _setBusy(true);
        try
        {
            var affected = await _session.SaveAsync();
            HasUnsavedChanges = false;
            _setStatus($"Saved {affected} change(s) to {Identifier}.");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Save failed", ex.Message +
                "\n\nIn-place editing requires the table to have a primary key.");
            _setStatus("Save failed.");
        }
        finally
        {
            _setBusy(false);
        }
    }

    // Reloads TPS data from disk without resetting columns/settings — used after a write
    // so the grid always reflects the actual file contents rather than in-memory edits.
    private async Task ReloadTpsSourceDataAsync()
    {
        if (_tpsPath is null || _tpsDef is null) return;
        var folder = Node.Connection.FilePath ?? "";
        if (_sourceData is not null)
        {
            _sourceData.RowChanged -= OnDataChanged;
            _sourceData.RowDeleted -= OnDataChanged;
        }
        _sourceData = await Task.Run(() => TpsService.ReadTable(folder, Node.Name, RowLimit));
        _sourceData.RowChanged += OnDataChanged;
        _sourceData.RowDeleted += OnDataChanged;
        ProjectView();
        HasUnsavedChanges = false;
    }

    private async Task SaveTpsChangesAsync()
    {
        if (_sourceData is null || _tpsDef is null || _tpsPath is null) return;
        var changes = _sourceData.GetChanges();
        if (changes is null) return;

        _setBusy(true);
        try
        {
            var edits = new List<TpsWriter.TpsRowEdit>();
            var skipped = new List<string>();

            foreach (DataRow row in changes.Rows)
            {
                switch (row.RowState)
                {
                    case DataRowState.Modified:
                        int rno = (int)row[TpsService.RecordNumberColumn, DataRowVersion.Original];
                        var fieldChanges = new List<TpsWriter.TpsFieldChange>();
                        foreach (DataColumn col in changes.Columns)
                        {
                            if (col.ColumnName == TpsService.RecordNumberColumn) continue;
                            var orig = row[col.ColumnName, DataRowVersion.Original];
                            var curr = row[col.ColumnName, DataRowVersion.Current];
                            if (!Equals(orig, curr))
                                fieldChanges.Add(new TpsWriter.TpsFieldChange(col.ColumnName, curr is DBNull ? null : curr));
                        }
                        if (fieldChanges.Count > 0)
                            edits.Add(new TpsWriter.TpsRowEdit(rno, fieldChanges));
                        break;
                    case DataRowState.Added:
                        skipped.Add("INSERT is not supported for TPS files.");
                        break;
                    case DataRowState.Deleted:
                        skipped.Add("DELETE is not supported for TPS files.");
                        break;
                }
            }

            var result = await Task.Run(() =>
                TpsWriter.SaveChanges(_tpsPath, _tpsDef, _tpsTableNumber, edits));

            // Reload from disk so the grid always reflects the true file contents.
            // This also correctly handles partial saves (some records failed re-encoding).
            await ReloadTpsSourceDataAsync();

            var msgs = new List<string> { $"Saved {result.Patched} record(s) to {Identifier}." };
            msgs.AddRange(result.Warnings);
            msgs.AddRange(skipped.Distinct());
            if (result.DiagnosticLogPath is not null)
                msgs.Add($"[Debug log: {result.DiagnosticLogPath}]");
            _setStatus(string.Join("  ", msgs));

            if (result.Warnings.Count > 0 || skipped.Count > 0)
                Dialogs.ShowError("TPS save — partial", string.Join("\n", result.Warnings.Concat(skipped.Distinct())));
            else if (result.Patched == 0 && edits.Count > 0)
                Dialogs.ShowError("TPS save — no records updated",
                    $"Attempted to save {edits.Count} change(s) but 0 records were updated and no specific error was detected.\n\n" +
                    $"This is unexpected. Please report this issue with the name of the TPS file and which fields you edited.");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("TPS save failed", ex.Message);
            _setStatus("TPS save failed.");
        }
        finally
        {
            _setBusy(false);
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this);

    public bool HasUnsavedChangesNow =>
        _session?.HasChanges ?? (_sourceData?.GetChanges() is not null && _tpsDef is not null);

    private void OnDataChanged(object? sender, DataRowChangeEventArgs e)
    {
        // Cheap dirty flag — DataTable.GetChanges() here would be O(rows) on every edit.
        HasUnsavedChanges = true;
        QueueSqlRefresh(); // live-update the SQL preview if it's open (debounced)
    }

    private void Detach()
    {
        if ((_tpsDef is not null || _isExcel) && _sourceData is not null)
        {
            _sourceData.RowChanged -= OnDataChanged;
            _sourceData.RowDeleted -= OnDataChanged;
        }
        _tpsDef = null;
        _tpsPath = null;
        _isExcel = false;

        _sourceData = null;
        if (_session is null) return;
        _session.Data.RowChanged -= OnDataChanged;
        _session.Data.RowDeleted -= OnDataChanged;
        _session.Dispose();
        _session = null;
    }

    public void Dispose() => Detach();
}
