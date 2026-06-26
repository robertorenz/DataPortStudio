using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionProfile _profile;
    private DatabaseEngine _engine = DatabaseEngine.SqlServer;

    public ConnectionDialog(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        LoadFromProfile();
    }

    private void LoadFromProfile()
    {
        NameBox.Text = _profile.Name;
        ServerBox.Text = _profile.Server;
        DatabaseBox.Text = _profile.Database ?? "";
        UserBox.Text = _profile.Username ?? "";
        PassBox.Password = _profile.Password ?? "";
        EncryptCheck.IsChecked = _profile.Encrypt;
        TrustCertCheck.IsChecked = _profile.TrustServerCertificate;
        FileBox.Text = _profile.FilePath ?? "";

        WinAuthRadio.IsChecked = _profile.IntegratedSecurity;
        SqlAuthRadio.IsChecked = !_profile.IntegratedSecurity;

        RawModeCheck.IsChecked = _profile.UseRawConnectionString;
        RawBox.Text = _profile.RawConnectionString ?? "";

        // Firebird fields (reuse Server/FilePath/Username/Password/Port).
        if (_profile.Engine == DatabaseEngine.Firebird)
        {
            FbHostBox.Text = string.IsNullOrWhiteSpace(_profile.Server) ? "localhost" : _profile.Server;
            FbFileBox.Text = _profile.FilePath ?? "";
            FbUserBox.Text = string.IsNullOrWhiteSpace(_profile.Username) ? "SYSDBA" : _profile.Username;
            FbPassBox.Password = _profile.Password ?? "";
            FbEmbeddedCheck.IsChecked = _profile.FirebirdEmbedded;
        }
        FbPortBox.Text = (_profile.Port > 0 ? _profile.Port : 3050).ToString();

        if (_profile.Engine == DatabaseEngine.MongoDb)
            MongoUriBox.Text = string.IsNullOrWhiteSpace(_profile.Server) ? "mongodb://localhost:27017" : _profile.Server;

        if (_profile.Engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb)
        {
            MyHostBox.Text = string.IsNullOrWhiteSpace(_profile.Server) ? "localhost" : _profile.Server;
            MyPortBox.Text = (_profile.Port > 0 ? _profile.Port : 3306).ToString();
            MyDatabaseBox.Text = _profile.Database ?? "";
            MyUserBox.Text = string.IsNullOrWhiteSpace(_profile.Username) ? "root" : _profile.Username;
            MyPassBox.Password = _profile.Password ?? "";
        }

        if (_profile.Engine == DatabaseEngine.Tps)
            TpsFolderBox.Text = _profile.FilePath ?? "";

        if (_profile.Engine == DatabaseEngine.ClarionDat)
            DatFolderBox.Text = _profile.FilePath ?? "";

        if (_profile.Engine == DatabaseEngine.Excel)
            ExcelFolderBox.Text = _profile.FilePath ?? "";

        if (_profile.Engine == DatabaseEngine.Oracle)
        {
            OraHostBox.Text = string.IsNullOrWhiteSpace(_profile.Server) ? "localhost" : _profile.Server;
            OraPortBox.Text = (_profile.Port > 0 ? _profile.Port : 1521).ToString();
            OraServiceBox.Text = _profile.Database ?? "";
            OraUserBox.Text = _profile.Username ?? "";
            OraPassBox.Password = _profile.Password ?? "";
        }

        if (_profile.Engine == DatabaseEngine.PostgreSql)
        {
            PgHostBox.Text = string.IsNullOrWhiteSpace(_profile.Server) ? "localhost" : _profile.Server;
            PgPortBox.Text = (_profile.Port > 0 ? _profile.Port : 5432).ToString();
            PgDatabaseBox.Text = _profile.Database ?? "";
            PgUserBox.Text = string.IsNullOrWhiteSpace(_profile.Username) ? "postgres" : _profile.Username;
            PgPassBox.Password = _profile.Password ?? "";
        }

        _engine = _profile.Engine;
        (_engine switch
        {
            DatabaseEngine.Sqlite => EngSqlite,
            DatabaseEngine.PostgreSql => EngPostgres,
            DatabaseEngine.MongoDb => EngMongo,
            DatabaseEngine.Firebird => EngFirebird,
            DatabaseEngine.MySql => EngMySql,
            DatabaseEngine.MariaDb => EngMariaDb,
            DatabaseEngine.Tps => EngTps,
            DatabaseEngine.ClarionDat => EngClarionDat,
            DatabaseEngine.Oracle => EngOracle,
            DatabaseEngine.Excel => EngExcel,
            _ => EngSqlServer
        }).IsChecked = true;

        ApplyAuthState();
        ApplyRawState();
        ApplyEngineState();
    }

    /// <summary>Writes the current form values into a profile (the edited copy or a temp).</summary>
    private void WriteToProfile(ConnectionProfile p)
    {
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unnamed Connection" : NameBox.Text.Trim();
        p.Engine = _engine;

        if (_engine == DatabaseEngine.Firebird)
        {
            p.Server = FbHostBox.Text.Trim();
            p.FilePath = string.IsNullOrWhiteSpace(FbFileBox.Text) ? null : FbFileBox.Text.Trim();
            p.Username = string.IsNullOrWhiteSpace(FbUserBox.Text) ? "SYSDBA" : FbUserBox.Text.Trim();
            p.Password = string.IsNullOrEmpty(FbPassBox.Password) ? null : FbPassBox.Password;
            p.Port = int.TryParse(FbPortBox.Text, out var port) ? port : 0;
            p.FirebirdEmbedded = FbEmbeddedCheck.IsChecked == true;
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.MongoDb)
        {
            p.Server = MongoUriBox.Text.Trim();
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb)
        {
            p.Server = string.IsNullOrWhiteSpace(MyHostBox.Text) ? "localhost" : MyHostBox.Text.Trim();
            p.Port = int.TryParse(MyPortBox.Text, out var myPort) ? myPort : 3306;
            p.Database = string.IsNullOrWhiteSpace(MyDatabaseBox.Text) ? null : MyDatabaseBox.Text.Trim();
            p.Username = string.IsNullOrWhiteSpace(MyUserBox.Text) ? "root" : MyUserBox.Text.Trim();
            p.Password = string.IsNullOrEmpty(MyPassBox.Password) ? null : MyPassBox.Password;
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.Tps)
        {
            p.FilePath = string.IsNullOrWhiteSpace(TpsFolderBox.Text) ? null : TpsFolderBox.Text.Trim();
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.ClarionDat)
        {
            p.FilePath = string.IsNullOrWhiteSpace(DatFolderBox.Text) ? null : DatFolderBox.Text.Trim();
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.Excel)
        {
            p.FilePath = string.IsNullOrWhiteSpace(ExcelFolderBox.Text) ? null : ExcelFolderBox.Text.Trim();
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.Oracle)
        {
            p.Server = string.IsNullOrWhiteSpace(OraHostBox.Text) ? "localhost" : OraHostBox.Text.Trim();
            p.Port = int.TryParse(OraPortBox.Text, out var oraPort) ? oraPort : 1521;
            p.Database = string.IsNullOrWhiteSpace(OraServiceBox.Text) ? null : OraServiceBox.Text.Trim();
            p.Username = string.IsNullOrWhiteSpace(OraUserBox.Text) ? null : OraUserBox.Text.Trim();
            p.Password = string.IsNullOrEmpty(OraPassBox.Password) ? null : OraPassBox.Password;
            p.UseRawConnectionString = false;
            return;
        }

        if (_engine == DatabaseEngine.PostgreSql)
        {
            p.Server = string.IsNullOrWhiteSpace(PgHostBox.Text) ? "localhost" : PgHostBox.Text.Trim();
            p.Port = int.TryParse(PgPortBox.Text, out var pgPort) ? pgPort : 5432;
            p.Database = string.IsNullOrWhiteSpace(PgDatabaseBox.Text) ? null : PgDatabaseBox.Text.Trim();
            p.Username = string.IsNullOrWhiteSpace(PgUserBox.Text) ? "postgres" : PgUserBox.Text.Trim();
            p.Password = string.IsNullOrEmpty(PgPassBox.Password) ? null : PgPassBox.Password;
            p.UseRawConnectionString = false;
            return;
        }

        p.Server = ServerBox.Text.Trim();
        p.Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? null : DatabaseBox.Text.Trim();
        p.IntegratedSecurity = WinAuthRadio.IsChecked == true;
        p.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();
        p.Password = string.IsNullOrEmpty(PassBox.Password) ? null : PassBox.Password;
        p.Encrypt = EncryptCheck.IsChecked == true;
        p.TrustServerCertificate = TrustCertCheck.IsChecked == true;
        p.UseRawConnectionString = RawModeCheck.IsChecked == true;
        p.RawConnectionString = string.IsNullOrWhiteSpace(RawBox.Text) ? null : RawBox.Text.Trim();
        p.FilePath = string.IsNullOrWhiteSpace(FileBox.Text) ? null : FileBox.Text.Trim();
    }

    // ---- UI state --------------------------------------------------------

    private void Engine_Changed(object sender, RoutedEventArgs e)
    {
        _engine = sender switch
        {
            var s when s == EngSqlite => DatabaseEngine.Sqlite,
            var s when s == EngPostgres => DatabaseEngine.PostgreSql,
            var s when s == EngMongo => DatabaseEngine.MongoDb,
            var s when s == EngFirebird => DatabaseEngine.Firebird,
            var s when s == EngMySql => DatabaseEngine.MySql,
            var s when s == EngMariaDb => DatabaseEngine.MariaDb,
            var s when s == EngTps => DatabaseEngine.Tps,
            var s when s == EngClarionDat => DatabaseEngine.ClarionDat,
            var s when s == EngOracle => DatabaseEngine.Oracle,
            var s when s == EngExcel => DatabaseEngine.Excel,
            _ => DatabaseEngine.SqlServer
        };
        ApplyEngineState();
    }

    private void ApplyEngineState()
    {
        if (SqlServerPanel is null) return;

        var isSql = _engine == DatabaseEngine.SqlServer;
        var isSqlite = _engine == DatabaseEngine.Sqlite;
        var isFirebird = _engine == DatabaseEngine.Firebird;
        var isMongo = _engine == DatabaseEngine.MongoDb;
        var isMySql = _engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb;
        var isTps = _engine == DatabaseEngine.Tps;
        var isDat = _engine == DatabaseEngine.ClarionDat;
        var isOracle = _engine == DatabaseEngine.Oracle;
        var isExcel = _engine == DatabaseEngine.Excel;
        var isPostgres = _engine == DatabaseEngine.PostgreSql;
        var supported = _engine.IsSupported();

        SqlServerPanel.Visibility = isSql ? Visibility.Visible : Visibility.Collapsed;
        SqlitePanel.Visibility = isSqlite ? Visibility.Visible : Visibility.Collapsed;
        FirebirdPanel.Visibility = isFirebird ? Visibility.Visible : Visibility.Collapsed;
        MongoPanel.Visibility = isMongo ? Visibility.Visible : Visibility.Collapsed;
        MySqlPanel.Visibility = isMySql ? Visibility.Visible : Visibility.Collapsed;
        TpsPanel.Visibility = isTps ? Visibility.Visible : Visibility.Collapsed;
        DatPanel.Visibility = isDat ? Visibility.Visible : Visibility.Collapsed;
        OraclePanel.Visibility = isOracle ? Visibility.Visible : Visibility.Collapsed;
        ExcelPanel.Visibility = isExcel ? Visibility.Visible : Visibility.Collapsed;
        PostgresPanel.Visibility = isPostgres ? Visibility.Visible : Visibility.Collapsed;
        if (isFirebird) ApplyFirebirdState();
        ComingSoonPanel.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        if (!supported)
            ComingSoonText.Text = $"{_engine.DisplayName()} support is coming soon. " +
                                  "You can still save this connection and it will be ready once support lands.";

        HeaderTitle.Text = $"{_engine.DisplayName()} Connection";
    }

    private void Auth_Changed(object sender, RoutedEventArgs e) => ApplyAuthState();

    private void ApplyAuthState()
    {
        if (CredentialsGrid is null) return;
        CredentialsGrid.IsEnabled = SqlAuthRadio.IsChecked == true;
    }

    private void RawMode_Changed(object sender, RoutedEventArgs e) => ApplyRawState();

    private void ApplyRawState()
    {
        if (RawPanel is null || FieldPanel is null) return;
        var raw = RawModeCheck.IsChecked == true;
        RawPanel.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        FieldPanel.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            FileBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "New Connection")
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void FbEmbedded_Changed(object sender, RoutedEventArgs e) => ApplyFirebirdState();

    private void ApplyFirebirdState()
    {
        if (FbServerRow is null) return;
        var embedded = FbEmbeddedCheck.IsChecked == true;
        FbServerRow.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        FbFileLabel.Content = embedded
            ? @"Database file  (local .fdb)"
            : @"Database  (server path or alias, e.g. C:\data\app.fdb)";
    }

    private void FbBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Firebird databases (*.fdb;*.gdb)|*.fdb;*.gdb|All files (*.*)|*.*",
            CheckFileExists = false
        };
        if (dlg.ShowDialog(this) == true)
        {
            FbFileBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "New Connection")
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void TpsBrowse_Click(object sender, RoutedEventArgs e) => BrowseFolderInto(TpsFolderBox, "Select the folder containing .tps files");

    private void DatBrowse_Click(object sender, RoutedEventArgs e) => BrowseFolderInto(DatFolderBox, "Select the folder containing .dat files");

    private void ExcelBrowse_Click(object sender, RoutedEventArgs e) => BrowseFolderInto(ExcelFolderBox, "Select the folder containing Excel files (.xls / .xlsx)");

    private void BrowseFolderInto(System.Windows.Controls.TextBox box, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(box.Text) && System.IO.Directory.Exists(box.Text))
            dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog(this) == true)
        {
            box.Text = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "New Connection")
                NameBox.Text = new System.IO.DirectoryInfo(dlg.FolderName).Name;
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ---- actions ---------------------------------------------------------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsSupported())
        {
            Dialogs.ShowMessage("Not supported yet",
                $"{_engine.DisplayName()} connections aren't supported yet — you can still save this one for later.");
            return;
        }

        var temp = new ConnectionProfile();
        WriteToProfile(temp);
        TestStatus.Text = "Testing…";
        TestStatus.Foreground = (Brush)FindResource("B.TextMuted");
        try
        {
            if (_engine == DatabaseEngine.Sqlite)
                await SqliteService.TestConnectionAsync(temp.BuildConnectionString());
            else if (_engine == DatabaseEngine.Firebird)
                await FirebirdService.TestConnectionAsync(temp.BuildConnectionString());
            else if (_engine == DatabaseEngine.MongoDb)
                await MongoService.TestConnectionAsync(temp.BuildConnectionString());
            else if (_engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb)
                await MySqlService.TestConnectionAsync(temp.BuildConnectionString());
            else if (_engine == DatabaseEngine.Tps)
                TpsService.TestConnection(temp.FilePath);
            else if (_engine == DatabaseEngine.ClarionDat)
                DatService.TestConnection(temp.FilePath);
            else if (_engine == DatabaseEngine.Excel)
                ExcelService.TestConnection(temp.FilePath);
            else if (_engine == DatabaseEngine.Oracle)
                await OracleService.TestConnectionAsync(temp.BuildConnectionString());
            else if (_engine == DatabaseEngine.PostgreSql)
                await PostgresService.TestConnectionAsync(temp.BuildConnectionString());
            else
                await SqlServerService.TestConnectionAsync(temp.BuildConnectionString());
            TestStatus.Text = "Connection succeeded.";
            TestStatus.Foreground = (Brush)FindResource("B.Success");
        }
        catch (Exception ex)
        {
            TestStatus.Text = "Failed.";
            TestStatus.Foreground = (Brush)FindResource("B.Danger");
            Dialogs.ShowError("Connection test failed", ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == DatabaseEngine.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(FileBox.Text))
            {
                Dialogs.ShowError("Missing file", "Please choose a SQLite database file.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.Firebird)
        {
            if (string.IsNullOrWhiteSpace(FbFileBox.Text))
            {
                Dialogs.ShowError("Missing database", "Please enter the Firebird database path or alias.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.MongoDb)
        {
            if (string.IsNullOrWhiteSpace(MongoUriBox.Text))
            {
                Dialogs.ShowError("Missing connection string", "Please enter a MongoDB connection string.");
                return;
            }
        }
        else if (_engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb)
        {
            if (string.IsNullOrWhiteSpace(MyHostBox.Text))
            {
                Dialogs.ShowError("Missing host", "Please enter a host name.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.Tps)
        {
            if (string.IsNullOrWhiteSpace(TpsFolderBox.Text))
            {
                Dialogs.ShowError("Missing folder", "Please choose the folder that contains your .tps files.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.ClarionDat)
        {
            if (string.IsNullOrWhiteSpace(DatFolderBox.Text))
            {
                Dialogs.ShowError("Missing folder", "Please choose the folder that contains your .dat files.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.Excel)
        {
            if (string.IsNullOrWhiteSpace(ExcelFolderBox.Text))
            {
                Dialogs.ShowError("Missing folder", "Please choose the folder that contains your Excel files.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.Oracle)
        {
            if (string.IsNullOrWhiteSpace(OraHostBox.Text) || string.IsNullOrWhiteSpace(OraServiceBox.Text))
            {
                Dialogs.ShowError("Missing details", "Please enter the Oracle host and service name.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.PostgreSql)
        {
            if (string.IsNullOrWhiteSpace(PgHostBox.Text))
            {
                Dialogs.ShowError("Missing host", "Please enter a PostgreSQL host name.");
                return;
            }
        }
        else if (_engine == DatabaseEngine.SqlServer)
        {
            if (RawModeCheck.IsChecked != true && string.IsNullOrWhiteSpace(ServerBox.Text))
            {
                Dialogs.ShowError("Missing server", "Please enter a server name.");
                return;
            }
        }

        WriteToProfile(_profile);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
