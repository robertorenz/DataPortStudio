using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DataPortStudio.Services;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

        var s = SettingsStore.Current;
        RowLimitBox.Text = s.DefaultRowLimit.ToString(CultureInfo.InvariantCulture);
        StructureCheck.IsChecked = s.ShowStructureByDefault;
        SqlCheck.IsChecked = s.ShowSqlByDefault;
        DetailCheck.IsChecked = s.ShowCellDetailByDefault;
        ClarionCheck.IsChecked = s.ShowClarionTypesByDefault;

        SectionCombo.ItemsSource = Enum.GetValues(typeof(InspectorSection));
        SectionCombo.SelectedItem = s.DefaultStructureSection;

        LanguageCombo.ItemsSource = new[]
        {
            new LanguageOption("en", "English"),
            new LanguageOption("es", "Español"),
        };
        LanguageCombo.SelectedValue = s.Language;
        if (LanguageCombo.SelectedItem is null) LanguageCombo.SelectedIndex = 0;

        ThemeCombo.ItemsSource = new[]
        {
            new LanguageOption("light", "Light"),
            new LanguageOption("dark",  "Dark"),
        };
        ThemeCombo.SelectedValue = s.Theme;
        if (ThemeCombo.SelectedItem is null) ThemeCombo.SelectedIndex = 0;
    }

    public record LanguageOption(string Code, string Name);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current.Clone();
        if (int.TryParse(RowLimitBox.Text, out var limit) && limit > 0)
            s.DefaultRowLimit = limit;
        s.ShowStructureByDefault = StructureCheck.IsChecked == true;
        s.ShowSqlByDefault = SqlCheck.IsChecked == true;
        s.ShowCellDetailByDefault = DetailCheck.IsChecked == true;
        s.ShowClarionTypesByDefault = ClarionCheck.IsChecked == true;
        if (SectionCombo.SelectedItem is InspectorSection section)
            s.DefaultStructureSection = section;
        if (LanguageCombo.SelectedValue is string lang)
            s.Language = lang;
        if (ThemeCombo.SelectedValue is string theme)
            s.Theme = theme;

        SettingsStore.Save(s);
        LocalizationManager.Instance.Language = s.Language;
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
