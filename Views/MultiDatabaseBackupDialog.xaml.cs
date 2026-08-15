using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;
using Microsoft.Win32;

namespace DataPortStudio.Views;

public partial class MultiDatabaseBackupDialog : Window
{
    private sealed class DatabaseChoice : INotifyPropertyChanged
    {
        private bool _isChecked;

        public required string Name { get; init; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<DatabaseChoice> _choices;

    public IReadOnlyList<string> SelectedDatabases => _choices
        .Where(choice => choice.IsChecked)
        .Select(choice => choice.Name)
        .ToList();

    public string DestinationFolder => DestinationBox.Text.Trim();

    public MultiDatabaseBackupDialog(
        ConnectionProfile connection,
        IReadOnlyCollection<string> databases,
        string? preferredDatabase = null)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } window ? window : null;
        ConnectionText.Text = $"{connection.Name} · {connection.Engine.DisplayName()}";
        SqlServerHint.Visibility = connection.Engine == DatabaseEngine.SqlServer
            ? Visibility.Visible
            : Visibility.Collapsed;

        _choices = databases
            .OrderBy(database => database, StringComparer.OrdinalIgnoreCase)
            .Select(database => new DatabaseChoice
            {
                Name = database,
                IsChecked = string.Equals(
                    database, preferredDatabase, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
        DatabaseList.ItemsSource = _choices;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(documents)) DestinationBox.Text = documents;
        UpdateSelection();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Instance["DbBackupMulti_BrowseTitle"]
        };
        if (Directory.Exists(DestinationBox.Text))
            dialog.InitialDirectory = DestinationBox.Text;
        if (dialog.ShowDialog(this) == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices) choice.IsChecked = true;
        UpdateSelection();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices) choice.IsChecked = false;
        UpdateSelection();
    }

    private void DatabaseCheck_Changed(object sender, RoutedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var count = _choices.Count(choice => choice.IsChecked);
        SelectionText.Text = string.Format(
            LocalizationManager.Instance["DbBackupMulti_Selected"], count, _choices.Count);
        BackupButton.IsEnabled = count > 0;
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        string L(string key) => LocalizationManager.Instance[key];
        if (_choices.All(choice => !choice.IsChecked))
        {
            Dialogs.ShowError(L("DbBackupMulti_Title"), L("DbBackupMulti_SelectRequired"));
            return;
        }

        if (!Directory.Exists(DestinationFolder))
        {
            Dialogs.ShowError(L("DbBackupMulti_Title"), L("DbBackupMulti_DestinationRequired"));
            DestinationBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
