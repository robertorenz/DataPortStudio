using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>
/// Shows an <see cref="ExecutionPlan"/> as an indented list of steps — each with its estimated rows
/// and share of the cost, the costly ones in red and the slow patterns flagged — plus a details
/// panel for the selected step and the engine's missing-index suggestions.
/// </summary>
public partial class ExecutionPlanView : UserControl
{
    /// <summary>One visible line of the tree.</summary>
    public sealed class PlanRow
    {
        public required PlanNode Node { get; init; }
        public int Depth { get; init; }
        public bool IsExpanded { get; set; } = true;
        public bool HasChildren => Node.Children.Count > 0;
        public string Glyph => HasChildren ? (IsExpanded ? "▾" : "▸") : "";
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public string Operation => Node.Operation;
        public string? Object => Node.Object;
        public bool HasWarnings => Node.Warnings.Count > 0;
        public string WarningText => string.Join("\n", Node.Warnings);
        public string RowsText => Node.EstRows is { } r ? FormatRows(r) : "";
        public bool HasShare => Node.Percent is not null;
        public double BarWidth => Math.Clamp(Node.Percent ?? 0, 0, 100);
        public string PercentText => Node.Percent is { } p ? p.ToString("0.#", CultureInfo.CurrentCulture) + " %" : "";
        public bool IsHot => Node.IsHot;
    }

    private ExecutionPlan? _plan;
    private readonly List<PlanNode> _roots = [];
    private readonly HashSet<PlanNode> _collapsed = [];
    private string _fileStem = "query";

    private static string L(string key) => LocalizationManager.Instance[key];

    public ExecutionPlanView() => InitializeComponent();

    public void Show(ExecutionPlan plan, string fileStem)
    {
        _plan = plan;
        _fileStem = fileStem;
        _roots.Clear();
        _collapsed.Clear();

        if (plan.Statements.Count == 1)
            _roots.Add(StatementRoot(plan.Statements[0], 0, wrap: false));
        else
            for (var i = 0; i < plan.Statements.Count; i++)
                _roots.Add(StatementRoot(plan.Statements[i], i + 1, wrap: true));

        var total = plan.Statements.Sum(s => s.TotalCost ?? 0);
        SummaryText.Text = string.Format(L("PL_Summary"), plan.Statements.Count) +
                           (plan.HasCosts && total > 0 ? "  ·  " + string.Format(L("PL_TotalCost"), FormatCost(total)) : "");
        var warnings = plan.WarningCount;
        WarningChip.Visibility = warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningChipText.Text = string.Format(L("PL_WarningCount"), warnings);
        ShareHeader.Text = plan.HasCosts ? L("PL_ColCost") : L("PL_ColRowShare");
        SaveButton.ToolTip = plan.RawExtension == ".sqlplan" ? L("PL_SaveSqlplanTip") : null;

        var suggestions = plan.Statements.SelectMany(s => s.Suggestions).Distinct().ToList();
        SuggestionPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionBox.Text = string.Join(Environment.NewLine, suggestions);

        Rebuild();
        // Open on the costliest step, so the eye lands where the time goes.
        var hottest = StepList.Items.Cast<PlanRow>().Where(r => r.Node.Percent is not null)
            .OrderByDescending(r => r.Node.Percent).FirstOrDefault();
        StepList.SelectedItem = hottest ?? StepList.Items.Cast<PlanRow>().FirstOrDefault();
    }

    private static PlanNode StatementRoot(PlanStatement s, int number, bool wrap)
    {
        PlanNode body;
        if (s.Root is not null) body = s.Root;
        else
        {
            body = new PlanNode { Operation = L("PL_CouldNotExplain"), Object = Preview(s.Sql) };
            if (s.Error is not null) body.Warnings.Add(s.Error);
        }
        if (!wrap) return body;

        var header = new PlanNode
        {
            Operation = string.Format(L("PL_Statement"), number),
            Object = Preview(s.Sql),
            Cost = s.TotalCost,
            Detail = s.Sql,
        };
        if (s.TotalCost is { } c) header.Properties.Add(new(L("PL_TotalCostLabel"), FormatCost(c)));
        header.Children.Add(body);
        return header;
    }

    private static string Preview(string sql)
    {
        var one = string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length > 90 ? one[..87] + "…" : one;
    }

    private void Rebuild()
    {
        var selected = (StepList.SelectedItem as PlanRow)?.Node;
        var rows = new List<PlanRow>();
        void Add(PlanNode n, int depth)
        {
            var row = new PlanRow { Node = n, Depth = depth, IsExpanded = !_collapsed.Contains(n) };
            rows.Add(row);
            if (row.IsExpanded) foreach (var c in n.Children) Add(c, depth + 1);
        }
        foreach (var r in _roots) Add(r, 0);
        StepList.ItemsSource = rows;
        if (selected is not null) StepList.SelectedItem = rows.FirstOrDefault(r => r.Node == selected);
    }

    private void Glyph_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlanRow { HasChildren: true } row) return;
        if (!_collapsed.Remove(row.Node)) _collapsed.Add(row.Node);
        e.Handled = true;
        Rebuild();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        _collapsed.Clear();
        Rebuild();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in _roots.SelectMany(r => r.DescendantsAndSelf()).Where(n => n.Children.Count > 0))
            _collapsed.Add(n);
        // Keep the top level open so there is something to read.
        foreach (var r in _roots) _collapsed.Remove(r);
        Rebuild();
    }

    // ── Details ─────────────────────────────────────────────────────────────

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailPanel.Children.Clear();
        if (StepList.SelectedItem is not PlanRow row) return;
        var n = row.Node;

        DetailPanel.Children.Add(new TextBlock
        {
            Text = n.Operation, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("B.Text"),
        });
        if (n.Object is not null)
            DetailPanel.Children.Add(new TextBlock
            {
                Text = n.Object, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("B.TextMuted"),
            });

        var figures = new List<string>();
        if (n.EstRows is { } rows) figures.Add(string.Format(L("PL_EstRows"), FormatRows(rows)));
        if (n.SelfCost is { } self) figures.Add(string.Format(L("PL_SelfCost"), FormatCost(self)));
        if (n.Cost is { } cost) figures.Add(string.Format(L("PL_SubtreeCost"), FormatCost(cost)));
        if (n.Percent is { } pct) figures.Add(string.Format(_plan?.HasCosts == true ? L("PL_ShareCost") : L("PL_ShareRows"),
            pct.ToString("0.#", CultureInfo.CurrentCulture)));
        if (figures.Count > 0)
            DetailPanel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", figures), Margin = new Thickness(0, 10, 0, 0), Foreground = Brush("B.Text"),
            });

        foreach (var w in n.Warnings)
            DetailPanel.Children.Add(new Border
            {
                Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = (Brush)Resources["Amber"],
                Background = Brush("B.SurfaceAlt"),
                Child = new TextBlock { Text = "⚠  " + w, TextWrapping = TextWrapping.Wrap, Foreground = Brush("B.Text") },
            });

        if (n.Detail is not null)
        {
            DetailPanel.Children.Add(Heading(L("PL_Condition")));
            DetailPanel.Children.Add(new TextBox
            {
                Text = n.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
                FontSize = 12, BorderThickness = new Thickness(0), Padding = new Thickness(6),
                Background = Brush("B.SurfaceAlt"), Foreground = Brush("B.Text"),
            });
        }

        if (n.Properties.Count > 0)
        {
            DetailPanel.Children.Add(Heading(L("PL_Properties")));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < n.Properties.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { Text = n.Properties[i].Key, Foreground = Brush("B.TextMuted"), FontSize = 12, Margin = new Thickness(0, 1, 10, 1) };
                var value = new TextBlock { Text = n.Properties[i].Value, Foreground = Brush("B.Text"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(key, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
                grid.Children.Add(key); grid.Children.Add(value);
            }
            DetailPanel.Children.Add(grid);
        }
    }

    private TextBlock Heading(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 14, 0, 4),
        Foreground = Brush("B.TextMuted"),
    };

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static string FormatRows(double r) =>
        r >= 10 ? Math.Round(r).ToString("N0", CultureInfo.CurrentCulture) : r.ToString("0.##", CultureInfo.CurrentCulture);

    private static string FormatCost(double c) =>
        c >= 100 ? c.ToString("N0", CultureInfo.CurrentCulture) : c.ToString("0.####", CultureInfo.CurrentCulture);

    // ── Raw plan ────────────────────────────────────────────────────────────

    private void CopyRaw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_plan?.Raw)) return;
        try { Clipboard.SetText(_plan.Raw); } catch { /* clipboard busy */ }
    }

    private void CopySuggestions_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SuggestionBox.Text); } catch { /* clipboard busy */ }
    }

    private void SaveRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || string.IsNullOrEmpty(_plan.Raw)) return;
        var ext = _plan.RawExtension;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L("PL_SaveRaw"),
            FileName = _fileStem + "_plan" + ext,
            DefaultExt = ext,
            Filter = ext switch
            {
                ".sqlplan" => "SQL Server plan (*.sqlplan)|*.sqlplan|XML (*.xml)|*.xml",
                ".json" => "JSON (*.json)|*.json",
                _ => "Text (*.txt)|*.txt",
            } + "|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _plan.Raw, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(L("PL_SaveRaw"), ex.Message);
        }
    }
}
