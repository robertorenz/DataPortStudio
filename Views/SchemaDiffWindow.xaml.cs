using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class SchemaDiffWindow : Window
{
    private readonly ConnectionProfile _connection;
    private readonly string _schema;

    public SchemaDiffWindow(ConnectionProfile connection, string? initialDb = null, string schema = "dbo")
    {
        InitializeComponent();
        _connection = connection;
        _schema = schema;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        Title = $"Schema Diff — {connection.Name}";
        _ = LoadDatabasesAsync(initialDb);
    }

    private async Task LoadDatabasesAsync(string? preselect)
    {
        StatusText.Text = "Loading databases…";
        try
        {
            var dbs = await SchemaDiffService.GetDatabasesAsync(_connection);
            LeftDbCombo.ItemsSource  = dbs;
            RightDbCombo.ItemsSource = dbs;
            if (preselect != null && dbs.Contains(preselect, StringComparer.OrdinalIgnoreCase))
                LeftDbCombo.SelectedItem = preselect;
            StatusText.Text = "Select two databases and click Compare.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error loading databases: " + ex.Message;
        }
    }

    private void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        CompareButton.IsEnabled = LeftDbCombo.SelectedItem != null && RightDbCombo.SelectedItem != null
            && !Equals(LeftDbCombo.SelectedItem, RightDbCombo.SelectedItem);
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var dbLeft  = LeftDbCombo.SelectedItem as string;
        var dbRight = RightDbCombo.SelectedItem as string;
        if (dbLeft is null || dbRight is null) return;

        CompareButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        StatusText.Text = "Comparing…";
        var sw = Stopwatch.StartNew();
        try
        {
            var diffs = await SchemaDiffService.CompareAsync(_connection, dbLeft, dbRight, _schema);
            BuildResults(diffs, dbLeft, dbRight);
            var changed = diffs.Count(d => d.Kind == DiffKind.ColumnsDiffer);
            var onlyL   = diffs.Count(d => d.Kind == DiffKind.OnlyInLeft);
            var onlyR   = diffs.Count(d => d.Kind == DiffKind.OnlyInRight);
            StatusText.Text = diffs.Count == 0
                ? $"✓ Schemas are identical  ·  {sw.ElapsedMilliseconds} ms"
                : $"{onlyL} only in left  ·  {onlyR} only in right  ·  {changed} tables differ  ·  {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally { CompareButton.IsEnabled = true; }
    }

    private void BuildResults(List<TableDiff> diffs, string dbLeft, string dbRight)
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
            (DiffKind.OnlyInLeft,    $"Only in LEFT  ({dbLeft})",   Color.FromRgb(0x42, 0x9a, 0xff)),
            (DiffKind.OnlyInRight,   $"Only in RIGHT  ({dbRight})", Color.FromRgb(0xff, 0x98, 0x00)),
            (DiffKind.ColumnsDiffer, "Column differences",          Color.FromRgb(0xef, 0x53, 0x50)),
        };

        foreach (var (kind, label, color) in groups)
        {
            var subset = diffs.Where(d => d.Kind == kind).ToList();
            if (subset.Count == 0) continue;

            // Section header
            var header = new Border
            {
                Background    = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
                BorderBrush   = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding       = new Thickness(10, 6, 10, 6),
                Margin        = new Thickness(0, 12, 0, 0),
                CornerRadius  = new CornerRadius(4, 4, 0, 0),
            };
            header.Child = new TextBlock
            {
                Text       = $"{label}  ({subset.Count})",
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                Foreground = new SolidColorBrush(color),
            };
            ResultsPanel.Children.Add(header);

            foreach (var diff in subset)
            {
                var tableRow = MakeExpandable(diff, color, dbLeft, dbRight);
                ResultsPanel.Children.Add(tableRow);
            }
        }
    }

    private static UIElement MakeExpandable(TableDiff diff, Color color, string dbLeft, string dbRight)
    {
        var outer = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 1, 0, 0),
        };

        var sp = new StackPanel();

        var headerBtn = new Button
        {
            Background    = new SolidColorBrush(Color.FromArgb(10, color.R, color.G, color.B)),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding       = new Thickness(12, 7, 12, 7),
            Cursor        = System.Windows.Input.Cursors.Hand,
        };
        var headerContent = new StackPanel { Orientation = Orientation.Horizontal };
        var chevron = new TextBlock { Text = diff.ColumnDiffs.Count > 0 ? "▶" : " ", FontSize = 10, Margin = new Thickness(0, 0, 8, 0) };
        headerContent.Children.Add(chevron);
        headerContent.Children.Add(new TextBlock
        {
            Text       = diff.TableName,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 13,
            Foreground = new SolidColorBrush(color),
        });
        if (diff.ColumnDiffs.Count > 0)
        {
            headerContent.Children.Add(new TextBlock
            {
                Text       = $"  — {diff.ColumnDiffs.Count} column difference(s)",
                FontSize   = 12,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
            });
        }
        headerBtn.Content = headerContent;
        sp.Children.Add(headerBtn);

        if (diff.ColumnDiffs.Count > 0)
        {
            var detail = BuildColumnDiffGrid(diff.ColumnDiffs, dbLeft, dbRight);
            detail.Visibility = Visibility.Collapsed;

            headerBtn.Click += (_, _) =>
            {
                var collapsed = detail.Visibility == Visibility.Collapsed;
                detail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = collapsed ? "▼" : "▶";
            };
            sp.Children.Add(detail);
        }

        outer.Child = sp;
        return outer;
    }

    private static UIElement BuildColumnDiffGrid(IReadOnlyList<ColumnDiff> diffs, string dbLeft, string dbRight)
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0)),
            Padding    = new Thickness(16, 8, 16, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void AddHeader(string text, int col)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["B.TextMuted"],
                Margin     = new Thickness(0, 0, 0, 4),
            };
            Grid.SetRow(tb, 0); Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeader("Column", 0);
        AddHeader(dbLeft,   1);
        AddHeader(dbRight,  3);

        for (int i = 0; i < diffs.Count; i++)
        {
            var d = diffs[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = i + 1;

            var bgColor = i % 2 == 0
                ? Color.FromArgb(10, 255, 255, 255)
                : Color.FromArgb(0, 0, 0, 0);

            UIElement Cell(string text, int col, Color? fg = null)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(bgColor),
                    Padding    = new Thickness(4, 3, 4, 3),
                };
                border.Child = new TextBlock
                {
                    Text       = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 12,
                    Foreground = fg.HasValue
                        ? new SolidColorBrush(fg.Value)
                        : (Brush)Application.Current.Resources["B.Text"],
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetRow(border, row); Grid.SetColumn(border, col);
                return border;
            }

            var leftText  = d.Left  is null ? "—  (missing)" : $"{d.Left.DataType}  {(d.Left.IsNullable ? "NULL" : "NOT NULL")}";
            var rightText = d.Right is null ? "—  (missing)" : $"{d.Right.DataType}  {(d.Right.IsNullable ? "NULL" : "NOT NULL")}";

            var leftColor  = d.Left  is null ? (Color?)Color.FromRgb(0xff, 0x98, 0x00) : null;
            var rightColor = d.Right is null ? (Color?)Color.FromRgb(0xff, 0x98, 0x00) : null;

            grid.Children.Add(Cell(d.Name,     0));
            grid.Children.Add(Cell(leftText,   1, leftColor));
            grid.Children.Add(Cell("→",        2));
            grid.Children.Add(Cell(rightText,  3, rightColor));
        }

        panel.Child = grid;
        return panel;
    }
}
