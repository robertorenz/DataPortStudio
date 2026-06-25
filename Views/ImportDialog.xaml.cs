using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using DataPortStudio.Models;
using DataPortStudio.Services;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class ImportDialog : Window
{
    public static string Skip => LocalizationManager.Instance["Import_Skip"];

    private readonly ConnectionProfile _connection;
    private readonly string _database;
    private readonly string _schema;
    private readonly string _table;

    private ImportService.ParsedFile? _file;
    private string? _path;

    public ObservableCollection<MappingRow> Rows { get; } = new();

    public ImportDialog(ConnectionProfile connection, string database, string schema, string table)
    {
        InitializeComponent();
        _connection = connection;
        _database = database;
        _schema = schema;
        _table = table;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        var loc = LocalizationManager.Instance;
        HeaderText.Text = $"{loc["Import_Title"]} → {schema}.{table}";
        FileBox.Text = loc["Import_ChooseFile"];
        MappingList.ItemsSource = Rows;
        Loaded += async (_, _) => await LoadColumnsAsync();
    }

    private async System.Threading.Tasks.Task LoadColumnsAsync()
    {
        try
        {
            var cols = await SqlServerService.GetColumnDetailsAsync(
                _connection.BuildConnectionString(), _database, _schema, _table);
            foreach (var c in cols)
            {
                if (c.Identity) continue; // auto-generated; can't insert
                Rows.Add(new MappingRow
                {
                    TableColumn = c.Name,
                    TypeLabel = $"{c.TypeName}{(c.Nullable ? "" : " ·NOT NULL")}",
                    SelectedSource = Skip
                });
            }
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Import", "Could not read table columns:\n" + ex.Message);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Data files (*.csv;*.tsv;*.txt;*.xlsx)|*.csv;*.tsv;*.txt;*.xlsx|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _path = dlg.FileName;
        FileBox.Text = _path;
        FileBox.Foreground = (System.Windows.Media.Brush)FindResource("B.Text");
        Reparse();
    }

    private void HeaderCheck_Click(object sender, RoutedEventArgs e) => Reparse();

    private void Reparse()
    {
        if (_path is null) return;
        try
        {
            _file = ImportService.Read(_path, HeaderCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Import", "Could not read the file:\n" + ex.Message);
            return;
        }

        var options = new List<string> { Skip };
        options.AddRange(_file.Headers);

        foreach (var row in Rows)
        {
            row.SourceOptions = options;
            // auto-map by case-insensitive name match
            var match = _file.Headers.FirstOrDefault(
                h => string.Equals(h, row.TableColumn, StringComparison.OrdinalIgnoreCase));
            row.SelectedSource = match ?? Skip;
        }

        PreviewText.Text = $"{_file.Rows.Count:N0} data row(s), {_file.Headers.Count} column(s) detected.";
        ImportButton.IsEnabled = _file.Rows.Count > 0;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        var mapping = Rows
            .Where(r => !string.IsNullOrEmpty(r.SelectedSource) && r.SelectedSource != Skip)
            .Select(r => (r.TableColumn, _file.Headers.IndexOf(r.SelectedSource!)))
            .Where(m => m.Item2 >= 0)
            .ToList();

        if (mapping.Count == 0)
        {
            Dialogs.ShowError("Import", "Map at least one column before importing.");
            return;
        }

        ImportButton.IsEnabled = false;
        ImportButton.Content = LocalizationManager.Instance["Import_Importing"];
        var (inserted, error) = await ImportService.ImportAsync(
            _connection.BuildConnectionString(), _database, _schema, _table, mapping, _file.Rows);
        ImportButton.Content = LocalizationManager.Instance["Import_Button"];
        ImportButton.IsEnabled = true;

        if (error is not null)
        {
            Dialogs.ShowError("Import failed", $"No rows were imported (the transaction was rolled back).\n\n{error}");
            return;
        }

        DialogResult = true;
        Close();
        Dialogs.ShowSuccess("Import complete", $"Imported {inserted:N0} row(s) into {_schema}.{_table}.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public partial class MappingRow : ObservableObject
{
    public string TableColumn { get; set; } = "";
    public string TypeLabel { get; set; } = "";

    [ObservableProperty] private List<string> _sourceOptions = new() { ImportDialog.Skip };
    [ObservableProperty] private string? _selectedSource = ImportDialog.Skip;
}
