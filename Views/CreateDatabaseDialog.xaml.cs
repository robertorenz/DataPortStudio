using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class CreateDatabaseDialog : Window
{
    private readonly ConnectionProfile _connection;
    private readonly DatabaseEngine _engine;
    private string _dataDirectory = "";
    private string _logDirectory = "";
    private string _lastDataLogicalName = "";
    private string _lastLogLogicalName = "";
    private string _lastDataPath = "";
    private string _lastLogPath = "";

    public DatabaseCreationOptions Options { get; private set; } = new();

    public CreateDatabaseDialog(ConnectionProfile connection)
    {
        InitializeComponent();
        _connection = connection;
        _engine = connection.Engine;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        if (_engine == DatabaseEngine.SqlServer) Width = 720;
        EngineText.Text = string.Format(
            LocalizationManager.Instance["DbCreate_Engine"],
            _engine.DisplayName());

        var panel = _engine switch
        {
            DatabaseEngine.SqlServer => SqlServerPanel,
            DatabaseEngine.PostgreSql => PostgresPanel,
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => MySqlPanel,
            DatabaseEngine.MongoDb => MongoPanel,
            _ => null
        };
        if (panel is not null) panel.Visibility = Visibility.Visible;
        PopulateCompatibilityLevels(160, 160);
        NameBox.TextChanged += (_, _) => UpdateSqlServerFileSuggestions();
        Loaded += Dialog_Loaded;
    }

    private async void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
        if (_engine != DatabaseEngine.SqlServer) return;

        string L(string key) => LocalizationManager.Instance[key];
        CreateButton.IsEnabled = false;
        SqlServerDefaultsStatus.Text = L("DbCreate_LoadingDefaults");
        try
        {
            var defaults = await SqlServerService.GetDatabaseCreationDefaultsAsync(
                _connection.BuildConnectionString());
            SelectComboTag(SqlServerRecoveryBox, defaults.RecoveryModel);
            PopulateCompatibilityLevels(
                MaxCompatibilityLevel(defaults.ProductMajorVersion, defaults.CompatibilityLevel),
                defaults.CompatibilityLevel);
            _dataDirectory = defaults.DataDirectory;
            _logDirectory = defaults.LogDirectory;
            SqlDataSizeBox.Text = defaults.DataInitialSizeMb.ToString();
            SqlDataMaxSizeBox.Text = defaults.DataMaxSizeMb?.ToString() ?? "";
            SqlDataGrowthBox.Text = defaults.DataGrowthMb.ToString();
            SqlLogSizeBox.Text = defaults.LogInitialSizeMb.ToString();
            SqlLogMaxSizeBox.Text = defaults.LogMaxSizeMb?.ToString() ?? "";
            SqlLogGrowthBox.Text = defaults.LogGrowthMb.ToString();
            UpdateSqlServerFileSuggestions();
            SqlServerDefaultsStatus.Text = L("DbCreate_DefaultsLoaded");
        }
        catch (Exception ex)
        {
            SqlServerDefaultsStatus.Text =
                string.Format(L("DbCreate_DefaultsFailed"), ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        string L(string key) => LocalizationManager.Instance[key];
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Dialogs.ShowError(L("DbCreate_Title"), L("DbCreate_NameRequired"));
            NameBox.Focus();
            return;
        }

        var initialCollection = MongoCollectionBox.Text.Trim();
        if (_engine == DatabaseEngine.MongoDb && string.IsNullOrEmpty(initialCollection))
        {
            Dialogs.ShowError(L("DbCreate_Title"), L("DbCreate_CollectionRequired"));
            MongoCollectionBox.Focus();
            return;
        }

        try
        {
            var selectedEncoding = (PostgresEncodingBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            Options = new DatabaseCreationOptions
            {
                Name = name,
                SqlServerCollation = NullIfWhiteSpace(SqlServerCollationBox.Text),
                SqlServerRecoveryModel = SelectedTag(SqlServerRecoveryBox, "FULL"),
                SqlServerCompatibilityLevel = SelectedIntTag(SqlServerCompatibilityBox, 160),
                SqlServerDataLogicalName = NullIfWhiteSpace(SqlDataLogicalNameBox.Text),
                SqlServerDataFilePath = NullIfWhiteSpace(SqlDataPathBox.Text),
                SqlServerDataInitialSizeMb = ParsePositive(SqlDataSizeBox, L("DbCreate_InitialSizeMb")),
                SqlServerDataMaxSizeMb = ParseOptionalPositive(SqlDataMaxSizeBox, L("DbCreate_MaxSizeMb")),
                SqlServerDataGrowthMb = ParsePositive(SqlDataGrowthBox, L("DbCreate_GrowthMb")),
                SqlServerLogLogicalName = NullIfWhiteSpace(SqlLogLogicalNameBox.Text),
                SqlServerLogFilePath = NullIfWhiteSpace(SqlLogPathBox.Text),
                SqlServerLogInitialSizeMb = ParsePositive(SqlLogSizeBox, L("DbCreate_InitialSizeMb")),
                SqlServerLogMaxSizeMb = ParseOptionalPositive(SqlLogMaxSizeBox, L("DbCreate_MaxSizeMb")),
                SqlServerLogGrowthMb = ParsePositive(SqlLogGrowthBox, L("DbCreate_GrowthMb")),
                PostgresOwner = NullIfWhiteSpace(PostgresOwnerBox.Text),
                PostgresEncoding = selectedEncoding ?? "UTF8",
                MySqlCharacterSet = string.IsNullOrWhiteSpace(MySqlCharsetBox.Text)
                    ? "utf8mb4"
                    : MySqlCharsetBox.Text.Trim(),
                MySqlCollation = NullIfWhiteSpace(MySqlCollationBox.Text),
                MongoInitialCollection = NullIfWhiteSpace(initialCollection)
            };

            if (_engine == DatabaseEngine.SqlServer
                && (string.IsNullOrWhiteSpace(Options.SqlServerDataFilePath)
                    != string.IsNullOrWhiteSpace(Options.SqlServerLogFilePath)))
                throw new ArgumentException(L("DbCreate_BothPathsRequired"));

            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            Dialogs.ShowError(L("DbCreate_Title"), ex.Message);
        }
    }

    private void UpdateSqlServerFileSuggestions()
    {
        if (_engine != DatabaseEngine.SqlServer) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var fileName = SanitizeFileName(name);
        UpdateSuggestedValue(SqlDataLogicalNameBox, ref _lastDataLogicalName, name);
        UpdateSuggestedValue(SqlLogLogicalNameBox, ref _lastLogLogicalName, name + "_log");
        UpdateSuggestedValue(SqlDataPathBox, ref _lastDataPath,
            CombineServerPath(_dataDirectory, fileName + ".mdf"));
        UpdateSuggestedValue(SqlLogPathBox, ref _lastLogPath,
            CombineServerPath(_logDirectory, fileName + "_log.ldf"));
    }

    private static void UpdateSuggestedValue(TextBox box, ref string previousSuggestion, string suggestion)
    {
        if (string.IsNullOrWhiteSpace(box.Text) || box.Text == previousSuggestion)
            box.Text = suggestion;
        previousSuggestion = suggestion;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string CombineServerPath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory)) return fileName;
        if (directory.EndsWith('\\') || directory.EndsWith('/')) return directory + fileName;
        var separator = directory.Contains('/') && !directory.Contains('\\') ? "/" : "\\";
        return directory + separator + fileName;
    }

    private void PopulateCompatibilityLevels(int maxLevel, int selectedLevel)
    {
        SqlServerCompatibilityBox.Items.Clear();
        var levels = new[] { 80, 90, 100, 110, 120, 130, 140, 150, 160, 170 }
            .Where(level => level <= maxLevel || level == selectedLevel)
            .Distinct()
            .OrderByDescending(level => level);
        foreach (var level in levels)
        {
            var item = new ComboBoxItem
            {
                Content = CompatibilityLabel(level),
                Tag = level
            };
            SqlServerCompatibilityBox.Items.Add(item);
            if (level == selectedLevel) SqlServerCompatibilityBox.SelectedItem = item;
        }
        if (SqlServerCompatibilityBox.SelectedIndex < 0 && SqlServerCompatibilityBox.Items.Count > 0)
            SqlServerCompatibilityBox.SelectedIndex = 0;
    }

    private static int MaxCompatibilityLevel(int majorVersion, int currentLevel) =>
        Math.Max(currentLevel, majorVersion switch
        {
            >= 17 => 170,
            16 => 160,
            15 => 150,
            14 => 140,
            13 => 130,
            12 => 120,
            11 => 110,
            10 => 100,
            9 => 90,
            _ => 80
        });

    private static string CompatibilityLabel(int level) => level switch
    {
        170 => "SQL Server 2025 (170)",
        160 => "SQL Server 2022 (160)",
        150 => "SQL Server 2019 (150)",
        140 => "SQL Server 2017 (140)",
        130 => "SQL Server 2016 (130)",
        120 => "SQL Server 2014 (120)",
        110 => "SQL Server 2012 (110)",
        100 => "SQL Server 2008 / 2008 R2 (100)",
        90 => "SQL Server 2005 (90)",
        _ => $"SQL Server ({level})"
    };

    private static void SelectComboTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int SelectedIntTag(ComboBox comboBox, int fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag is int value ? value : fallback;

    private static int ParsePositive(TextBox box, string field)
    {
        if (int.TryParse(box.Text.Trim(), out var value) && value > 0) return value;
        box.Focus();
        throw new ArgumentException($"{field}: {LocalizationManager.Instance["DbCreate_PositiveNumber"]}");
    }

    private static int? ParseOptionalPositive(TextBox box, string field)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        return ParsePositive(box, field);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
