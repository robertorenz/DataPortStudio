using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

/// <summary>A multi-select column dropdown rendered as removable ordered chips.</summary>
public partial class IndexColumnPicker : UserControl
{
    private bool _synchronizing;

    public ObservableCollection<string> Tokens { get; } = new();
    public ObservableCollection<string> Choices { get; } = new();

    public static readonly DependencyProperty AvailableColumnsProperty = DependencyProperty.Register(
        nameof(AvailableColumns), typeof(IEnumerable<DesignColumn>), typeof(IndexColumnPicker),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedColumnsProperty = DependencyProperty.Register(
        nameof(SelectedColumns), typeof(string), typeof(IndexColumnPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            SelectedColumnsChanged));

    public IEnumerable<DesignColumn>? AvailableColumns
    {
        get => (IEnumerable<DesignColumn>?)GetValue(AvailableColumnsProperty);
        set => SetValue(AvailableColumnsProperty, value);
    }

    public string SelectedColumns
    {
        get => (string)GetValue(SelectedColumnsProperty);
        set => SetValue(SelectedColumnsProperty, value);
    }

    public IndexColumnPicker()
    {
        InitializeComponent();
        SynchronizeTokens(SelectedColumns);
    }

    private static void SelectedColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (IndexColumnPicker)d;
        if (!picker._synchronizing)
            picker.SynchronizeTokens(e.NewValue as string ?? "");
    }

    private void SynchronizeTokens(string value)
    {
        _synchronizing = true;
        try
        {
            Tokens.Clear();
            foreach (var name in value.Split(',')
                         .Select(name => name.Trim())
                         .Where(name => name.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Tokens.Add(name);
            RefreshChoices();
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void CommitTokens()
    {
        _synchronizing = true;
        try
        {
            SetCurrentValue(SelectedColumnsProperty, string.Join(", ", Tokens));
        }
        finally
        {
            _synchronizing = false;
        }
        RefreshChoices();
    }

    private void RefreshChoices()
    {
        var selected = Tokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = AvailableColumns?
            .Select(column => column.Name.Trim())
            .Where(name => name.Length > 0 && !selected.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        Choices.Clear();
        foreach (var name in names) Choices.Add(name);
    }

    private void ColumnPicker_DropDownOpened(object sender, EventArgs e) => RefreshChoices();

    private void ColumnPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColumnPicker.SelectedItem is not string name) return;
        if (!Tokens.Contains(name, StringComparer.OrdinalIgnoreCase)) Tokens.Add(name);
        ColumnPicker.SelectedItem = null;
        CommitTokens();
    }

    private void RemoveToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        var token = Tokens.FirstOrDefault(value =>
            value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (token is null) return;
        Tokens.Remove(token);
        CommitTokens();
    }
}
