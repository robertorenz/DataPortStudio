using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class ColumnChoice : ObservableObject
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Display => Enabled ? Name : Name + "   (can't match — large/text column)";
    [ObservableProperty] private bool isChecked;
}

public partial class RowIdentityDialog : Window
{
    private readonly EditableTableSession _session;

    public ObservableCollection<ColumnChoice> Columns { get; } = new();
    /// <summary>Result: null = use all columns (default); otherwise the chosen columns.</summary>
    public List<string>? SelectedColumns { get; private set; }

    public RowIdentityDialog(EditableTableSession session)
    {
        InitializeComponent();
        _session = session;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        var nonComparable = new HashSet<string>(session.NonComparableColumns, StringComparer.OrdinalIgnoreCase);
        var current = new HashSet<string>(
            session.IsCustomIdentity ? session.KeyColumnNames : Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in session.AllColumnNames)
        {
            var enabled = !nonComparable.Contains(name);
            Columns.Add(new ColumnChoice
            {
                Name = name,
                Enabled = enabled,
                IsChecked = enabled && current.Contains(name)
            });
        }

        ColumnList.ItemsSource = Columns;
        UseAllCheck.IsChecked = !session.IsCustomIdentity;
        ApplyUseAllState();
    }

    private void ApplyUseAllState()
    {
        if (ColumnList is not null)
            ColumnList.IsEnabled = UseAllCheck.IsChecked != true;
    }

    private void UseAll_Changed(object sender, RoutedEventArgs e) => ApplyUseAllState();

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var detected = new HashSet<string>(_session.DetectIdentityColumns(), StringComparer.OrdinalIgnoreCase);
        if (detected.Count == 0)
        {
            Dialogs.ShowMessage("Detect", "Couldn't find a unique combination in the loaded rows.");
            return;
        }
        UseAllCheck.IsChecked = false;
        foreach (var c in Columns)
            c.IsChecked = c.Enabled && detected.Contains(c.Name);
        ApplyUseAllState();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (UseAllCheck.IsChecked == true)
        {
            SelectedColumns = null;
        }
        else
        {
            var selected = Columns.Where(c => c.IsChecked && c.Enabled).Select(c => c.Name).ToList();
            if (selected.Count == 0)
            {
                Dialogs.ShowError("Pick a column", "Select at least one column, or choose \"Use all columns\".");
                return;
            }
            SelectedColumns = selected;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
