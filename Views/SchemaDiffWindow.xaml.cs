using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class SchemaDiffWindow : Window
{
    private sealed record ConnectionChoice(string Label, ConnectionProfile? Connection, bool IsNew = false);
    private sealed record SelectableDiff(CheckBox CheckBox, TableDiff Diff,
        bool CanLeftToRight, bool CanRightToLeft);

    private readonly List<ConnectionProfile> _connections;
    private readonly ConnectionProfile _initialConnection;
    private readonly string? _initialDatabase;
    private readonly string? _initialSchema;
    private readonly Action<ConnectionProfile>? _connectionAdded;
    private readonly List<SelectableDiff> _selectable = [];
    private List<ConnectionChoice> _connectionChoices = [];
    private Guid? _lastLeftConnectionId;
    private Guid? _lastRightConnectionId;
    private bool _updatingEndpoints;

    public SchemaDiffWindow(ConnectionProfile connection, string? initialDb = null, string schema = "dbo")
        : this([connection], connection, initialDb)
    {
        _initialSchema = schema;
    }

    public SchemaDiffWindow(IEnumerable<ConnectionProfile> connections,
        ConnectionProfile initialConnection, string? initialDb = null,
        Action<ConnectionProfile>? connectionAdded = null)
    {
        InitializeComponent();
        _connections = connections.Where(SchemaDiffService.IsSupported).DistinctBy(c => c.Id).ToList();
        _initialConnection = initialConnection;
        _initialDatabase = initialDb;
        _initialSchema = null;
        _connectionAdded = connectionAdded;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        Title = "Schema Diff — compare database endpoints";
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _updatingEndpoints = true;
        try
        {
            RebuildConnectionChoices();
            LeftConnectionCombo.SelectedItem = ChoiceFor(_initialConnection.Id)
                ?? _connectionChoices.FirstOrDefault(c => !c.IsNew);
            var initialLeftId = SelectedConnection(left: true)?.Id;
            RightConnectionCombo.SelectedItem = _connectionChoices.FirstOrDefault(c =>
                    !c.IsNew && c.Connection?.Id != initialLeftId)
                ?? LeftConnectionCombo.SelectedItem;
            _lastLeftConnectionId = SelectedConnection(left: true)?.Id;
            _lastRightConnectionId = SelectedConnection(left: false)?.Id;

            await LoadDatabasesAsync(left: true, _initialDatabase, _initialSchema);
            await LoadDatabasesAsync(left: false, null);

            // With one connection, make the most useful initial choice: two different databases.
            if (_connections.Count == 1 && Equals(LeftDbCombo.SelectedItem, RightDbCombo.SelectedItem) &&
                RightDbCombo.Items.Count > 1)
                RightDbCombo.SelectedIndex = 1;
            await LoadSchemasAsync(left: false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error loading endpoints: " + ex.Message;
        }
        finally
        {
            _updatingEndpoints = false;
            UpdateCompareState();
        }
    }

    private async void LeftConnection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpoints || !IsLoaded) return;
        if (await HandleNewConnectionAsync(left: true)) return;
        _lastLeftConnectionId = SelectedConnection(left: true)?.Id;
        await ReloadDatabasesAsync(left: true);
    }

    private async void RightConnection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpoints || !IsLoaded) return;
        if (await HandleNewConnectionAsync(left: false)) return;
        _lastRightConnectionId = SelectedConnection(left: false)?.Id;
        await ReloadDatabasesAsync(left: false);
    }

    private void RebuildConnectionChoices(Guid? selectLeft = null, Guid? selectRight = null)
    {
        _connectionChoices = _connections
            .Select(c => new ConnectionChoice($"{c.Name} ({c.Engine.DisplayName()})", c))
            .Append(new ConnectionChoice("＋ New connection…", null, IsNew: true))
            .ToList();
        LeftConnectionCombo.ItemsSource = _connectionChoices;
        RightConnectionCombo.ItemsSource = _connectionChoices;
        if (selectLeft.HasValue) LeftConnectionCombo.SelectedItem = ChoiceFor(selectLeft.Value);
        if (selectRight.HasValue) RightConnectionCombo.SelectedItem = ChoiceFor(selectRight.Value);
    }

    private ConnectionChoice? ChoiceFor(Guid id) =>
        _connectionChoices.FirstOrDefault(c => c.Connection?.Id == id);

    private ConnectionProfile? SelectedConnection(bool left) =>
        ((left ? LeftConnectionCombo : RightConnectionCombo).SelectedItem as ConnectionChoice)?.Connection;

    /// <summary>
    /// Turns the synthetic picker row into a modal connection-creation flow.  Cancelling restores
    /// the previous selection and never attempts to load databases from an empty profile.
    /// </summary>
    private async Task<bool> HandleNewConnectionAsync(bool left)
    {
        var combo = left ? LeftConnectionCombo : RightConnectionCombo;
        if (combo.SelectedItem is not ConnectionChoice { IsNew: true }) return false;

        var previousId = left ? _lastLeftConnectionId : _lastRightConnectionId;
        _updatingEndpoints = true;
        combo.SelectedItem = previousId.HasValue ? ChoiceFor(previousId.Value) : null;
        _updatingEndpoints = false;

        var profile = new ConnectionProfile();
        var dialog = new ConnectionDialog(profile) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            UpdateCompareState();
            return true;
        }

        _connectionAdded?.Invoke(profile);
        if (!SchemaDiffService.IsSupported(profile))
        {
            Dialogs.ShowMessage("Connection saved",
                $"'{profile.Name}' was saved, but {profile.Engine.DisplayName()} cannot be used in Schema Diff.");
            UpdateCompareState();
            return true;
        }

        _connections.Add(profile);
        var leftId = left ? profile.Id : _lastLeftConnectionId;
        var rightId = left ? _lastRightConnectionId : profile.Id;
        _updatingEndpoints = true;
        RebuildConnectionChoices(leftId, rightId);
        _lastLeftConnectionId = SelectedConnection(left: true)?.Id;
        _lastRightConnectionId = SelectedConnection(left: false)?.Id;
        _updatingEndpoints = false;
        await ReloadDatabasesAsync(left);
        return true;
    }

    private async Task ReloadDatabasesAsync(bool left)
    {
        _updatingEndpoints = true;
        try { await LoadDatabasesAsync(left, null); }
        catch (Exception ex) { StatusText.Text = "Error loading databases: " + ex.Message; }
        finally { _updatingEndpoints = false; UpdateCompareState(); }
    }

    private async void LeftDatabase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpoints || !IsLoaded) return;
        await ReloadSchemasAsync(left: true);
    }

    private async void RightDatabase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpoints || !IsLoaded) return;
        await ReloadSchemasAsync(left: false);
    }

    private async Task ReloadSchemasAsync(bool left)
    {
        _updatingEndpoints = true;
        try { await LoadSchemasAsync(left); }
        catch (Exception ex) { StatusText.Text = "Error loading schemas: " + ex.Message; }
        finally { _updatingEndpoints = false; UpdateCompareState(); }
    }

    private void Endpoint_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingEndpoints) UpdateCompareState();
    }

    private async Task LoadDatabasesAsync(bool left, string? preselect, string? schemaPreselect = null)
    {
        var connection = SelectedConnection(left);
        var dbCombo = left ? LeftDbCombo : RightDbCombo;
        if (connection is null)
        {
            dbCombo.ItemsSource = null;
            return;
        }

        StatusText.Text = $"Loading databases from {connection.Name}…";
        var databases = await SchemaDiffService.GetDatabasesAsync(connection);
        dbCombo.ItemsSource = databases;
        dbCombo.SelectedItem = preselect is not null
            ? databases.FirstOrDefault(d => d.Equals(preselect, StringComparison.OrdinalIgnoreCase))
            : null;
        if (dbCombo.SelectedItem is null && databases.Count > 0) dbCombo.SelectedIndex = 0;
        await LoadSchemasAsync(left, schemaPreselect);
        StatusText.Text = "Select the two endpoints and click Compare.";
    }

    private async Task LoadSchemasAsync(bool left, string? preselect = null)
    {
        var connection = SelectedConnection(left);
        var database = (left ? LeftDbCombo : RightDbCombo).SelectedItem as string;
        var combo = left ? LeftSchemaCombo : RightSchemaCombo;
        if (connection is null || database is null)
        {
            combo.ItemsSource = null;
            return;
        }

        var schemas = await SchemaDiffService.GetSchemasAsync(connection, database);
        combo.ItemsSource = schemas;
        var preferred = preselect ?? (connection.Engine switch
        {
            DatabaseEngine.SqlServer => "dbo",
            DatabaseEngine.PostgreSql => "public",
            _ => schemas.FirstOrDefault()
        });
        combo.SelectedItem = schemas.FirstOrDefault(s =>
            s.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? schemas.FirstOrDefault();
    }

    private SchemaEndpoint? Endpoint(bool left)
    {
        var connection = SelectedConnection(left);
        var database = (left ? LeftDbCombo : RightDbCombo).SelectedItem as string;
        var schema = (left ? LeftSchemaCombo : RightSchemaCombo).SelectedItem as string;
        return connection is null || database is null || schema is null
            ? null
            : new SchemaEndpoint(connection, database, schema);
    }

    private void UpdateCompareState()
    {
        var left = Endpoint(true);
        var right = Endpoint(false);
        CompareButton.IsEnabled = left is not null && right is not null &&
            !(left.Connection.Id == right.Connection.Id &&
              left.Database.Equals(right.Database, StringComparison.OrdinalIgnoreCase) &&
              left.Schema.Equals(right.Schema, StringComparison.OrdinalIgnoreCase));
    }

    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private async Task CompareAsync()
    {
        var left = Endpoint(true);
        var right = Endpoint(false);
        if (left is null || right is null) return;

        SetBusy(true);
        ResultsPanel.Children.Clear();
        _selectable.Clear();
        StatusText.Text = "Comparing table definitions…";
        var sw = Stopwatch.StartNew();
        try
        {
            var objectTypes = SelectedObjectTypes();
            if (objectTypes.Count == 0)
            {
                StatusText.Text = "Select at least one object type to compare.";
                return;
            }
            var diffs = await SchemaDiffService.CompareAsync(
                left, right, RespectOrderCheck.IsChecked == true, objectTypes);
            BuildResults(diffs, left, right);
            var changed = diffs.Count(d => d.Kind is DiffKind.ColumnsDiffer or DiffKind.DefinitionDiffers);
            var onlyL = diffs.Count(d => d.Kind == DiffKind.OnlyInLeft);
            var onlyR = diffs.Count(d => d.Kind == DiffKind.OnlyInRight);
            StatusText.Text = diffs.Count == 0
                ? $"✓ Schemas are identical  ·  {sw.ElapsedMilliseconds} ms"
                : $"{onlyL} only in left  ·  {onlyR} only in right  ·  {changed} objects differ  ·  {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Comparison failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            UpdateSelectionState();
        }
    }

    private List<SchemaObjectType> SelectedObjectTypes()
    {
        var result = new List<SchemaObjectType>();
        if (TablesCheck.IsChecked == true) result.Add(SchemaObjectType.Table);
        if (ViewsCheck.IsChecked == true) result.Add(SchemaObjectType.View);
        if (FunctionsCheck.IsChecked == true) result.Add(SchemaObjectType.Function);
        if (ProceduresCheck.IsChecked == true) result.Add(SchemaObjectType.Procedure);
        return result;
    }

    private void SetBusy(bool busy)
    {
        CompareButton.IsEnabled = !busy;
        LeftConnectionCombo.IsEnabled = !busy;
        RightConnectionCombo.IsEnabled = !busy;
        LeftDbCombo.IsEnabled = !busy;
        RightDbCombo.IsEnabled = !busy;
        LeftSchemaCombo.IsEnabled = !busy;
        RightSchemaCombo.IsEnabled = !busy;
        TablesCheck.IsEnabled = !busy;
        ViewsCheck.IsEnabled = !busy;
        FunctionsCheck.IsEnabled = !busy;
        ProceduresCheck.IsEnabled = !busy;
        RespectOrderCheck.IsEnabled = !busy;
        if (busy)
        {
            SelectAllCheck.IsEnabled = false;
            CopyLeftToRightButton.IsEnabled = false;
            CopyRightToLeftButton.IsEnabled = false;
        }
        else UpdateCompareState();
    }

    private void BuildResults(List<TableDiff> diffs, SchemaEndpoint left, SchemaEndpoint right)
    {
        if (diffs.Count == 0)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = "✓  No differences found — the two schemas are identical.",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50)),
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }

        var groups = new[]
        {
            (DiffKind.OnlyInLeft, $"Only in LEFT  ({left.DisplayName})", Color.FromRgb(0x42, 0x9a, 0xff)),
            (DiffKind.OnlyInRight, $"Only in RIGHT  ({right.DisplayName})", Color.FromRgb(0xff, 0x98, 0x00)),
            (DiffKind.ColumnsDiffer, "Column differences", Color.FromRgb(0xef, 0x53, 0x50)),
            (DiffKind.DefinitionDiffers, "Definition differences", Color.FromRgb(0xab, 0x47, 0xbc)),
        };

        foreach (var (kind, label, color) in groups)
        {
            var subset = diffs.Where(d => d.Kind == kind).ToList();
            if (subset.Count == 0) continue;
            ResultsPanel.Children.Add(SectionHeader($"{label}  ({subset.Count})", color));
            foreach (var diff in subset)
                ResultsPanel.Children.Add(MakeExpandable(diff, color, left, right));
        }
    }

    private static UIElement SectionHeader(string label, Color color)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
        };
        header.Child = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(color),
        };
        return header;
    }

    private UIElement MakeExpandable(TableDiff diff, Color color,
        SchemaEndpoint left, SchemaEndpoint right)
    {
        var outer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 0, 0),
        };
        var stack = new StackPanel();
        var header = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(10, color.R, color.G, color.B)),
            Margin = new Thickness(0),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        {
            var canLeftToRight = false;
            var canRightToLeft = false;
            var leftReason = "Not available for LEFT → RIGHT.";
            var rightReason = "Not available for RIGHT → LEFT.";
            if (diff.Kind != DiffKind.OnlyInRight)
                canLeftToRight = SchemaObjectTransferService.CanTransfer(
                    left, right, diff, sourceIsLeft: true, out leftReason);
            if (diff.Kind != DiffKind.OnlyInLeft)
                canRightToLeft = SchemaObjectTransferService.CanTransfer(
                    right, left, diff, sourceIsLeft: false, out rightReason);

            var canTransfer = canLeftToRight || canRightToLeft;
            var reason = canLeftToRight && canRightToLeft
                ? $"LEFT → RIGHT: {leftReason}\nRIGHT → LEFT: {rightReason}"
                : canLeftToRight ? $"LEFT → RIGHT: {leftReason}"
                : canRightToLeft ? $"RIGHT → LEFT: {rightReason}"
                : diff.Kind == DiffKind.ColumnsDiffer ? leftReason : $"{leftReason}\n{rightReason}";

            var check = new CheckBox
            {
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = canTransfer,
                ToolTip = reason
            };
            check.Click += (_, _) => UpdateSelectionState();
            if (canTransfer)
                _selectable.Add(new SelectableDiff(check, diff, canLeftToRight, canRightToLeft));
            Grid.SetColumn(check, 0);
            header.Children.Add(check);
        }

        var hasDetails = diff.ColumnDiffs.Count > 0 || diff.Kind == DiffKind.DefinitionDiffers;
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(diff.Kind is DiffKind.ColumnsDiffer or DiffKind.DefinitionDiffers ? 12 : 2, 7, 12, 7),
            Cursor = hasDetails
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow,
        };
        Grid.SetColumn(button, 1);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var chevron = new TextBlock
        {
            Text = hasDetails ? "▶" : " ",
            FontSize = 10,
            Margin = new Thickness(0, 0, 8, 0)
        };
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock
        {
            Text = $"{diff.ObjectType.DisplayName()}  ·  {diff.TableName}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(color),
        });
        if (diff.ColumnDiffs.Count > 0)
            content.Children.Add(new TextBlock
            {
                Text = $"  — {diff.ColumnDiffs.Count} column difference(s)",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
            });
        else if (diff.Kind == DiffKind.DefinitionDiffers)
            content.Children.Add(new TextBlock
            {
                Text = "  — SQL definition differs",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
            });
        button.Content = content;
        header.Children.Add(button);
        stack.Children.Add(header);

        if (hasDetails)
        {
            var detail = diff.Kind == DiffKind.DefinitionDiffers
                ? BuildDefinitionDiffGrid(diff.LeftDefinition, diff.RightDefinition,
                    left.DisplayName, right.DisplayName)
                : BuildColumnDiffGrid(diff.ColumnDiffs, left.DisplayName, right.DisplayName);
            detail.Visibility = Visibility.Collapsed;
            button.Click += (_, _) =>
            {
                var show = detail.Visibility == Visibility.Collapsed;
                detail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = show ? "▼" : "▶";
            };
            stack.Children.Add(detail);
        }
        outer.Child = stack;
        return outer;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectAllCheck.IsChecked == true;
        foreach (var item in _selectable) item.CheckBox.IsChecked = selected;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectAllCheck.IsEnabled = _selectable.Count > 0;
        var selected = _selectable.Where(x => x.CheckBox.IsChecked == true).ToList();
        CopyLeftToRightButton.IsEnabled = selected.Any(x => x.CanLeftToRight);
        CopyRightToLeftButton.IsEnabled = selected.Any(x => x.CanRightToLeft);
        SelectAllCheck.IsChecked = _selectable.Count > 0 && selected.Count == _selectable.Count
            ? true
            : selected.Count == 0 ? false : null;
    }

    private async void CopyLeftToRight_Click(object sender, RoutedEventArgs e) =>
        await CopySelectedAsync(leftToRight: true);

    private async void CopyRightToLeft_Click(object sender, RoutedEventArgs e) =>
        await CopySelectedAsync(leftToRight: false);

    private async Task CopySelectedAsync(bool leftToRight)
    {
        var left = Endpoint(true);
        var right = Endpoint(false);
        if (left is null || right is null) return;
        var objects = _selectable.Where(x => x.CheckBox.IsChecked == true &&
                                             (leftToRight ? x.CanLeftToRight : x.CanRightToLeft))
            .Select(x => x.Diff)
            .OrderBy(x => x.ObjectType switch
            {
                SchemaObjectType.Table => 0,
                SchemaObjectType.Function => 1,
                SchemaObjectType.View => 2,
                SchemaObjectType.Procedure => 3,
                _ => 4
            })
            .ThenBy(x => x.TableName)
            .ToList();
        if (objects.Count == 0) return;

        var source = leftToRight ? left : right;
        var target = leftToRight ? right : left;
        var withData = IncludeDataCheck.IsChecked == true;
        var tableCount = objects.Count(x => x.ObjectType == SchemaObjectType.Table);
        var replacementCount = objects.Count(x => x.Kind == DiffKind.DefinitionDiffers);
        var tableChangeCount = objects.Count(x => x.Kind == DiffKind.ColumnsDiffer);
        var summary = string.Join(", ", objects.GroupBy(x => x.ObjectType)
            .Select(g => $"{g.Count()} {g.Key.DisplayName().ToLowerInvariant()}(s)"));
        var dataText = tableCount == 0 ? "" :
            $"\nTable data: {(withData ? "included" : "structure only")}";
        var replacementText = replacementCount == 0 ? "" :
            $"\n\nWarning: {replacementCount} existing destination definition(s) will be replaced.";
        var tableChangeText = tableChangeCount == 0 ? "" :
            $"\n\nWarning: columns in {tableChangeCount} existing table(s) will be altered transactionally.";
        if (!Dialogs.Confirm("Transfer selected objects",
                $"Transfer {objects.Count} object(s) ({summary}) from\n{source.DisplayName}\n\nto\n{target.DisplayName}?{dataText}{replacementText}{tableChangeText}"))
            return;

        SetBusy(true);
        var copied = 0;
        var skipped = new List<string>();
        var failed = new List<string>();
        try
        {
            for (var index = 0; index < objects.Count; index++)
            {
                var obj = objects[index];
                var display = $"{obj.ObjectType.DisplayName()} {obj.TableName}";
                StatusText.Text = $"Transferring {display} ({index + 1}/{objects.Count})…";
                try
                {
                    // Recheck immediately before creation: a compare result may be stale.
                    if ((obj.Kind is DiffKind.OnlyInLeft or DiffKind.OnlyInRight) &&
                        await SchemaObjectTransferService.ExistsAsync(target, obj))
                    {
                        skipped.Add(display);
                        continue;
                    }
                    await SchemaObjectTransferService.TransferAsync(
                        source, target, obj, withData, sourceIsLeft: leftToRight);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{display}: {ex.Message}");
                }
            }

            if (failed.Count == 0)
                Dialogs.ShowSuccess("Schema synchronization",
                    $"Transferred {copied} object(s)." +
                    (skipped.Count > 0 ? $"\nSkipped {skipped.Count} object(s) already present." : ""));
            else
                Dialogs.ShowError("Schema synchronization",
                    $"Transferred {copied} object(s); {failed.Count} failed.\n\n" + string.Join("\n", failed));
        }
        finally
        {
            SetBusy(false);
            await CompareAsync();
        }
    }

    private enum DiffLineKind { Equal, Added, Removed, Changed }

    private sealed record DiffOperation(char Kind, string Text, int Number);

    private sealed record DiffLine(
        string? LeftText, int? LeftNumber, string? RightText, int? RightNumber, DiffLineKind Kind);

    /// <summary>Renders a line-aligned, Git-style side-by-side definition diff.</summary>
    private static UIElement BuildDefinitionDiffGrid(
        string? leftDefinition, string? rightDefinition, string leftName, string rightName)
    {
        var lines = BuildLineDiff(leftDefinition, rightDefinition);
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0)),
            Padding = new Thickness(16, 10, 16, 12),
        };
        var horizontal = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 330,
        };
        var grid = new Grid { MinWidth = 1120 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Header(string text, int column, int span = 1)
        {
            var header = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
                Margin = new Thickness(4, 0, 4, 5),
                ToolTip = text,
            };
            Grid.SetColumn(header, column);
            Grid.SetColumnSpan(header, span);
            grid.Children.Add(header);
        }

        Header(leftName, 0, 3);
        Header(rightName, 4, 3);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var row = i + 1;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (leftBg, rightBg) = line.Kind switch
            {
                DiffLineKind.Removed => (Color.FromArgb(55, 0xd7, 0x3a, 0x49), Colors.Transparent),
                DiffLineKind.Added => (Colors.Transparent, Color.FromArgb(55, 0x2e, 0xb8, 0x5c)),
                DiffLineKind.Changed => (Color.FromArgb(45, 0xd7, 0x3a, 0x49), Color.FromArgb(45, 0x2e, 0xb8, 0x5c)),
                _ => (Colors.Transparent, Colors.Transparent)
            };

            UIElement Cell(string? text, int? number, int column, Color background, string marker = "")
            {
                var cell = new Border
                {
                    Background = new SolidColorBrush(background),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 2, 4, 2),
                };
                var content = new TextBlock
                {
                    Text = column is 0 or 4
                        ? number?.ToString() ?? ""
                        : column is 1 or 5 ? marker : text ?? "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["B.Text"],
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = column is 0 or 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };
                if (column is 1 or 5)
                {
                    content.Foreground = marker == "+"
                        ? new SolidColorBrush(Color.FromRgb(0x1b, 0x7a, 0x3d))
                        : marker == "−" ? new SolidColorBrush(Color.FromRgb(0xa5, 0x1e, 0x2b))
                        : (Brush)Application.Current.Resources["B.TextMuted"];
                    content.FontWeight = FontWeights.Bold;
                }
                cell.Child = content;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                return cell;
            }

            grid.Children.Add(Cell(line.LeftText, line.LeftNumber, 0, leftBg));
            grid.Children.Add(Cell(null, null, 1, leftBg,
                line.Kind is DiffLineKind.Removed or DiffLineKind.Changed ? "−" : ""));
            grid.Children.Add(Cell(line.LeftText, null, 2, leftBg));
            grid.Children.Add(Cell(null, null, 3, Colors.Transparent));
            grid.Children.Add(Cell(line.RightText, line.RightNumber, 4, rightBg));
            grid.Children.Add(Cell(null, null, 5, rightBg,
                line.Kind is DiffLineKind.Added or DiffLineKind.Changed ? "+" : ""));
            grid.Children.Add(Cell(line.RightText, null, 6, rightBg));
        }

        horizontal.Content = grid;
        panel.Child = horizontal;
        return panel;
    }

    private static List<DiffLine> BuildLineDiff(string? leftDefinition, string? rightDefinition)
    {
        var left = SplitDefinition(leftDefinition);
        var right = SplitDefinition(rightDefinition);
        var n = left.Count;
        var m = right.Count;
        var operations = new List<DiffOperation>();

        // LCS gives stable alignment for ordinary SQL definitions. Avoid excessive memory for very
        // large generated routines by falling back to a line-by-line changed view.
        if ((long)n * m > 4_000_000)
        {
            var count = Math.Max(n, m);
            return Enumerable.Range(0, count)
                .Select(i => new DiffLine(
                    i < n ? left[i] : null, i < n ? i + 1 : null,
                    i < m ? right[i] : null, i < m ? i + 1 : null,
                    i < n && i < m ? DiffLineKind.Changed : i < n ? DiffLineKind.Removed : DiffLineKind.Added))
                .ToList();
        }

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = left[i] == right[j] ? lcs[i + 1, j + 1] + 1 :
                    Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var li = 0;
        var ri = 0;
        while (li < n || ri < m)
        {
            if (li < n && ri < m && left[li] == right[ri])
            {
                operations.Add(new('=', left[li], li + 1));
                li++; ri++;
            }
            else if (ri == m || (li < n && lcs[li + 1, ri] >= lcs[li, ri + 1]))
                operations.Add(new('-', left[li], li++ + 1));
            else
                operations.Add(new('+', right[ri], ri++ + 1));
        }

        var result = new List<DiffLine>();
        for (var i = 0; i < operations.Count;)
        {
            if (operations[i].Kind == '=')
            {
                var op = operations[i++];
                result.Add(new DiffLine(op.Text, op.Number, op.Text, op.Number, DiffLineKind.Equal));
                continue;
            }

            var removed = new List<DiffOperation>();
            var added = new List<DiffOperation>();
            while (i < operations.Count && operations[i].Kind == '-') removed.Add(operations[i++]);
            while (i < operations.Count && operations[i].Kind == '+') added.Add(operations[i++]);
            if (removed.Count == 0 && added.Count == 0)
            {
                var op = operations[i++];
                (op.Kind == '-' ? removed : added).Add(op);
            }
            var count = Math.Max(removed.Count, added.Count);
            for (var p = 0; p < count; p++)
                result.Add(new DiffLine(
                    p < removed.Count ? removed[p].Text : null,
                    p < removed.Count ? removed[p].Number : null,
                    p < added.Count ? added[p].Text : null,
                    p < added.Count ? added[p].Number : null,
                    removed.Count > 0 && added.Count > 0 ? DiffLineKind.Changed :
                    removed.Count > 0 ? DiffLineKind.Removed : DiffLineKind.Added));
        }
        return result;
    }

    private static List<string> SplitDefinition(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return ["— Definition unavailable —"];
        return definition.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
    }

    private static UIElement BuildColumnDiffGrid(
        IReadOnlyList<ColumnDiff> diffs, string leftName, string rightName)
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0)),
            Padding = new Thickness(16, 8, 16, 8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Header(string text, int column)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = text,
            };
            Grid.SetColumn(tb, column);
            grid.Children.Add(tb);
        }
        Header("Column", 0);
        Header(leftName, 1);
        Header(rightName, 3);

        for (var i = 0; i < diffs.Count; i++)
        {
            var d = diffs[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = i + 1;
            var bg = i % 2 == 0 ? Color.FromArgb(10, 255, 255, 255) : Colors.Transparent;

            UIElement Cell(string text, int column, Color? foreground = null)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(bg),
                    Padding = new Thickness(4, 3, 4, 3),
                };
                border.Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = foreground.HasValue
                        ? new SolidColorBrush(foreground.Value)
                        : (Brush)Application.Current.Resources["B.Text"],
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetRow(border, row);
                Grid.SetColumn(border, column);
                return border;
            }

            string Describe(ColumnInfo? c) => c is null
                ? "—  (missing)"
                : $"#{c.Ordinal}  {c.DataType}  {(c.IsNullable ? "NULL" : "NOT NULL")}";
            var warning = Color.FromRgb(0xff, 0x98, 0x00);
            grid.Children.Add(Cell(d.Name + (d.OrderDiffers ? "  (order)" : ""), 0,
                d.OrderDiffers ? warning : null));
            grid.Children.Add(Cell(Describe(d.Left), 1, d.Left is null ? warning : null));
            grid.Children.Add(Cell("→", 2));
            grid.Children.Add(Cell(Describe(d.Right), 3, d.Right is null ? warning : null));
        }
        panel.Child = grid;
        return panel;
    }
}
