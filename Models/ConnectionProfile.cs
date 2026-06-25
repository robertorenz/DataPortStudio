using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace DataPortStudio.Models;

/// <summary>
/// A saved database connection. Either built from individual fields,
/// or supplied as a raw connection string. Supports multiple engines.
/// </summary>
public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Connection";

    /// <summary>Which database engine this connection targets.</summary>
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;

    public string Server { get; set; } = "";
    public string? Database { get; set; }

    /// <summary>SQLite database file path, or Firebird database path/alias.</summary>
    public string? FilePath { get; set; }

    /// <summary>TCP port (Firebird; 0 = engine default 3050).</summary>
    public int Port { get; set; }

    /// <summary>Firebird embedded mode (no server; loads the engine from native DLLs next to the app).</summary>
    public bool FirebirdEmbedded { get; set; }

    /// <summary>True = Windows Authentication, False = SQL Server login.</summary>
    public bool IntegratedSecurity { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;

    public bool UseRawConnectionString { get; set; }
    public string? RawConnectionString { get; set; }

    /// <summary>Builds the effective connection string for this connection's engine.</summary>
    public string BuildConnectionString()
    {
        if (UseRawConnectionString && !string.IsNullOrWhiteSpace(RawConnectionString))
            return RawConnectionString!;

        switch (Engine)
        {
            case DatabaseEngine.Sqlite:
                return new SqliteConnectionStringBuilder { DataSource = FilePath ?? "" }.ToString();
            case DatabaseEngine.Tps:
            case DatabaseEngine.ClarionDat:
                // No real connection string — Clarion files are read directly from a folder.
                return FilePath ?? "";
            case DatabaseEngine.Oracle:
                // Easy Connect: Data Source = host:port/service_name.
                var oraHost = string.IsNullOrWhiteSpace(Server) ? "localhost" : Server.Trim();
                var oraPort = Port > 0 ? Port : 1521;
                var service = string.IsNullOrWhiteSpace(Database) ? "" : Database!.Trim();
                return $"User Id={Username};Password={Password};" +
                       $"Data Source={oraHost}:{oraPort}/{service};Connection Timeout=15;";
            case DatabaseEngine.MongoDb:
                // The MongoDB URI is stored verbatim in Server (e.g. mongodb://localhost:27017).
                return string.IsNullOrWhiteSpace(Server) ? "mongodb://localhost:27017" : Server.Trim();
            case DatabaseEngine.MySql:
            case DatabaseEngine.MariaDb:
                var my = new MySqlConnectionStringBuilder
                {
                    Server = string.IsNullOrWhiteSpace(Server) ? "localhost" : Server,
                    Port = (uint)(Port > 0 ? Port : 3306),
                    UserID = string.IsNullOrWhiteSpace(Username) ? "root" : Username,
                    Password = Password ?? "",
                    Database = Database ?? "",
                    AllowUserVariables = true,
                    ConnectionTimeout = 15
                };
                return my.ConnectionString;
            case DatabaseEngine.Firebird:
                var fb = new FbConnectionStringBuilder
                {
                    Database = FilePath ?? "",
                    UserID = string.IsNullOrWhiteSpace(Username) ? "SYSDBA" : Username,
                    Password = string.IsNullOrEmpty(Password) ? "masterkey" : Password,
                    Charset = "UTF8",
                    Dialect = 3,
                    Pooling = false
                };
                if (FirebirdEmbedded)
                {
                    // No server: the Firebird engine is loaded in-process from fbclient.dll
                    // (and plugins) placed next to the application.
                    fb.ServerType = FbServerType.Embedded;
                    fb.ClientLibrary = "fbclient.dll";
                }
                else
                {
                    fb.ServerType = FbServerType.Default;
                    fb.DataSource = string.IsNullOrWhiteSpace(Server) ? "localhost" : Server;
                    fb.Port = Port > 0 ? Port : 3050;
                }
                return fb.ToString();
            case DatabaseEngine.SqlServer:
                break; // built below
            default:
                throw new NotSupportedException($"{Engine.DisplayName()} connections are not supported yet.");
        }

        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 15,
            ApplicationName = "DataPortStudio"
        };

        if (!string.IsNullOrWhiteSpace(Database))
            b.InitialCatalog = Database;

        if (IntegratedSecurity)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = Username ?? "";
            b.Password = Password ?? "";
        }

        return b.ConnectionString;
    }

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();
}
