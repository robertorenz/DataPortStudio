using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class ExportDialog : Window
{
    private readonly DataView _view;
    private readonly Func<string, object?, string?>? _display;
    private readonly string _suggestedName;
    private ExportFormat _format = ExportFormat.Csv;

    public ObservableCollection<ColumnChoice> Columns { get; } = new();

    public ExportDialog(DataView view, string suggestedName, Func<string, object?, string?>? display = null)
    {
        InitializeComponent();
        _view = view;
        _display = display;
        _suggestedName = suggestedName;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        foreach (ExportFormat f in Enum.GetValues(typeof(ExportFormat)))
        {
            var rb = new RadioButton
            {
                Content = ExportService.Label(f),
                GroupName = "Format",
                Tag = f,
                Margin = new Thickness(4, 4, 4, 4),
                IsChecked = f == _format,
            };
            rb.Checked += (_, _) =>
            {
                _format = (ExportFormat)rb.Tag;
                JsonPanel.Visibility = _format == ExportFormat.Json ? Visibility.Visible : Visibility.Collapsed;
                SqlPanel.Visibility  = _format == ExportFormat.Sql  ? Visibility.Visible : Visibility.Collapsed;
            };
            FormatList.Children.Add(rb);
        }

        // JSON option combos.
        DateOrderCombo.ItemsSource = Enum.GetValues(typeof(DateOrder));
        DateOrderCombo.SelectedItem = DateOrder.DMY;
        BinaryCombo.ItemsSource = Enum.GetValues(typeof(BinaryEncoding));
        BinaryCombo.SelectedItem = BinaryEncoding.Base64;

        // Rows-to-export scope, only meaningful when the grid is filtered.
        var filtered = _view.Count;
        var total = _view.Table?.Rows.Count ?? filtered;
        if (!string.IsNullOrEmpty(_view.RowFilter) && total != filtered)
        {
            FilteredRadio.Content = $"Filtered rows only ({filtered:N0})";
            AllRadio.Content = $"All rows ({total:N0})";
            ScopePanel.Visibility = Visibility.Visible;
        }

        foreach (DataColumn c in view.Table!.Columns)
            Columns.Add(new ColumnChoice { Name = c.ColumnName, Enabled = true, IsChecked = true });
        ColumnList.ItemsSource = Columns;
    }

    private ExportOptions BuildOptions() => new()
    {
        JsonLegacyRecordsKey = JsonLegacyCheck.IsChecked == true,
        ZeroPaddingDate = ZeroPadCheck.IsChecked == true,
        DateOrder = DateOrderCombo.SelectedItem is DateOrder d ? d : DateOrder.DMY,
        DateDelimiter = string.IsNullOrEmpty(DateDelimBox.Text) ? "/" : DateDelimBox.Text,
        TimeDelimiter = string.IsNullOrEmpty(TimeDelimBox.Text) ? ":" : TimeDelimBox.Text,
        DecimalSymbol = string.IsNullOrEmpty(DecimalBox.Text) ? "." : DecimalBox.Text,
        Binary = BinaryCombo.SelectedItem is BinaryEncoding b ? b : BinaryEncoding.Base64,
        SqlBatchSize = int.TryParse(SqlBatchBox.Text, out var bs) && bs >= 0 ? bs : 100,
        SqlNoCount = SqlNoCountCheck.IsChecked == true,
    };

    /// <summary>The view to export from, honoring the All/Filtered choice.</summary>
    private DataView ScopedView()
    {
        if (AllRadio.IsChecked == true && _view.Table is not null)
            return new DataView(_view.Table) { Sort = _view.Sort };
        return _view;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var c in Columns) c.IsChecked = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var c in Columns) c.IsChecked = false; }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var cols = Columns.Where(c => c.IsChecked).Select(c => c.Name).ToList();
        if (cols.Count == 0)
        {
            Dialogs.ShowError("No columns", "Select at least one column to export.");
            return;
        }
        var format = _format;

        var ext = ExportService.Extension(format);
        var dialog = new SaveFileDialog
        {
            FileName = $"{Sanitize(_suggestedName)}.{ext}",
            DefaultExt = ext,
            Filter = $"{ExportService.Label(format)}|*.{ext}|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var tableName = _suggestedName.Split('.').Last();
            var view = ScopedView();
            ExportService.Export(view, cols, format, dialog.FileName, HeadersCheck.IsChecked == true,
                _display, tableName, BuildOptions());
            DialogResult = true;
            Close();
            Dialogs.ExportComplete(dialog.FileName, view.Count);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Export failed", ex.Message);
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
