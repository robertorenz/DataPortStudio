using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

/// <summary>Creates and restores engine-native database backup files.</summary>
public static class DatabaseMaintenanceService
{
    public sealed record BackupFileType(string Extension, string Filter);

    public static bool Supports(DatabaseEngine engine) =>
        engine is DatabaseEngine.SqlServer or DatabaseEngine.Sqlite or DatabaseEngine.PostgreSql
            or DatabaseEngine.MongoDb or DatabaseEngine.Firebird or DatabaseEngine.MySql
            or DatabaseEngine.MariaDb or DatabaseEngine.Oracle;

    /// <summary>Engines whose server connection can expose more than one database.</summary>
    public static bool SupportsMultiple(DatabaseEngine engine) =>
        engine is DatabaseEngine.SqlServer or DatabaseEngine.PostgreSql
            or DatabaseEngine.MySql or DatabaseEngine.MariaDb or DatabaseEngine.MongoDb;

    /// <summary>Lists the databases available for a multi-database backup.</summary>
    public static async Task<List<string>> GetDatabasesAsync(ConnectionProfile connection)
    {
        var connectionString = connection.BuildConnectionString();
        var databases = connection.Engine switch
        {
            DatabaseEngine.SqlServer => await SqlServerService.GetDatabasesAsync(connectionString),
            DatabaseEngine.PostgreSql => await PostgresService.GetDatabasesAsync(connectionString),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                await MySqlService.GetDatabasesAsync(connectionString),
            DatabaseEngine.MongoDb => await MongoService.ListDatabasesAsync(connectionString),
            _ => throw new NotSupportedException()
        };

        // tempdb is recreated whenever SQL Server starts and cannot be backed up.
        if (connection.Engine == DatabaseEngine.SqlServer)
            databases.RemoveAll(database =>
                string.Equals(database, "tempdb", StringComparison.OrdinalIgnoreCase));

        return databases.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(database => database, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static BackupFileType GetFileType(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => new(".bak", "SQL Server backup (*.bak)|*.bak"),
        DatabaseEngine.PostgreSql => new(".backup", "PostgreSQL backup (*.backup)|*.backup"),
        DatabaseEngine.MySql or DatabaseEngine.MariaDb => new(".sql", "SQL dump (*.sql)|*.sql"),
        DatabaseEngine.MongoDb => new(".archive.gz", "MongoDB archive (*.archive.gz)|*.archive.gz"),
        DatabaseEngine.Sqlite => new(".sqlite", "SQLite database (*.sqlite;*.db)|*.sqlite;*.db"),
        DatabaseEngine.Firebird => new(".fbk", "Firebird backup (*.fbk)|*.fbk"),
        DatabaseEngine.Oracle => new(".dmp", "Oracle dump (*.dmp)|*.dmp"),
        _ => new(".backup", "Backup files (*.backup)|*.backup")
    };

    public static Task BackupAsync(ConnectionProfile connection, string database, string path) =>
        connection.Engine switch
        {
            DatabaseEngine.SqlServer => BackupSqlServerAsync(connection, database, path),
            DatabaseEngine.Sqlite => BackupSqliteAsync(connection, path),
            DatabaseEngine.PostgreSql => BackupPostgresAsync(connection, database, path),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => BackupMySqlAsync(connection, database, path),
            DatabaseEngine.MongoDb => BackupMongoAsync(connection, database, path),
            DatabaseEngine.Firebird => BackupFirebirdAsync(connection, path),
            DatabaseEngine.Oracle => BackupOracleAsync(connection, path),
            _ => throw new NotSupportedException()
        };

    public static Task RestoreAsync(ConnectionProfile connection, string database, string path) =>
        connection.Engine switch
        {
            DatabaseEngine.SqlServer => RestoreSqlServerAsync(connection, database, path),
            DatabaseEngine.Sqlite => RestoreSqliteAsync(connection, path),
            DatabaseEngine.PostgreSql => RestorePostgresAsync(connection, database, path),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => RestoreMySqlAsync(connection, database, path),
            DatabaseEngine.MongoDb => RestoreMongoAsync(connection, database, path),
            DatabaseEngine.Firebird => RestoreFirebirdAsync(connection, path),
            DatabaseEngine.Oracle => RestoreOracleAsync(connection, path),
            _ => throw new NotSupportedException()
        };

    private static async Task BackupSqlServerAsync(ConnectionProfile profile, string database, string path)
    {
        await using var connection = new SqlConnection(
            SqlServerService.WithDatabase(profile.BuildConnectionString(), "master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = $"BACKUP DATABASE {QuoteSqlServer(database)} TO DISK = @path " +
                              "WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10";
        command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RestoreSqlServerAsync(ConnectionProfile profile, string database, string path)
    {
        await using var connection = new SqlConnection(
            SqlServerService.WithDatabase(profile.BuildConnectionString(), "master"));
        await connection.OpenAsync();
        var quoted = QuoteSqlServer(database);
        await ValidateSqlServerBackupAsync(connection, database, path);
        try
        {
            await ExecuteSqlServerMaintenanceAsync(connection,
                $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            await using var restore = connection.CreateCommand();
            restore.CommandTimeout = 0;
            restore.CommandText = $"RESTORE DATABASE {quoted} FROM DISK = @path " +
                                  "WITH REPLACE, RECOVERY, STATS = 10";
            restore.Parameters.AddWithValue("@path", path);
            await restore.ExecuteNonQueryAsync();
            await ExecuteSqlServerMaintenanceAsync(connection,
                $"ALTER DATABASE {quoted} SET MULTI_USER");
        }
        catch
        {
            try
            {
                await ExecuteSqlServerMaintenanceAsync(connection,
                    $"ALTER DATABASE {quoted} SET MULTI_USER");
            }
            catch { }
            throw;
        }
    }

    private static async Task ValidateSqlServerBackupAsync(
        SqlConnection connection, string database, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "RESTORE HEADERONLY FROM DISK = @path";
        command.Parameters.AddWithValue("@path", path);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                LocalizationManager.Instance["DbRestore_InvalidBackup"]);
        var sourceDatabase = reader.GetString(reader.GetOrdinal("DatabaseName"));
        if (!string.Equals(sourceDatabase, database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.Format(
                LocalizationManager.Instance["DbRestore_WrongDatabase"], sourceDatabase, database));
    }

    private static async Task ExecuteSqlServerMaintenanceAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteSqlServer(string identifier) =>
        "[" + identifier.Replace("]", "]]") + "]";

    private static async Task BackupSqliteAsync(ConnectionProfile profile, string path)
    {
        await using var source = new SqliteConnection(profile.BuildConnectionString());
        await using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    private static async Task RestoreSqliteAsync(ConnectionProfile profile, string path)
    {
        await using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await using var destination = new SqliteConnection(profile.BuildConnectionString());
        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    private static Task BackupPostgresAsync(ConnectionProfile profile, string database, string path)
    {
        var cs = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString());
        return RunExternalAsync(["pg_dump"],
            ["--host", cs.Host ?? "", "--port", cs.Port.ToString(), "--username", cs.Username ?? "",
             "--format=custom", "--no-owner", "--file", path, database],
            new Dictionary<string, string?> { ["PGPASSWORD"] = cs.Password });
    }

    private static Task RestorePostgresAsync(ConnectionProfile profile, string database, string path)
    {
        var cs = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString());
        return RunExternalAsync(["pg_restore"],
            ["--host", cs.Host ?? "", "--port", cs.Port.ToString(), "--username", cs.Username ?? "",
             "--dbname", database, "--clean", "--if-exists", "--no-owner", path],
            new Dictionary<string, string?> { ["PGPASSWORD"] = cs.Password });
    }

    private static Task BackupMySqlAsync(ConnectionProfile profile, string database, string path)
    {
        var cs = new MySqlConnectionStringBuilder(profile.BuildConnectionString());
        var tools = profile.Engine == DatabaseEngine.MariaDb
            ? new[] { "mariadb-dump", "mysqldump" }
            : new[] { "mysqldump", "mariadb-dump" };
        return RunExternalAsync(tools,
            ["--host", cs.Server, "--port", cs.Port.ToString(), "--user", cs.UserID,
             "--single-transaction", "--routines", "--events", "--triggers",
             $"--result-file={path}", "--databases", database],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = cs.Password });
    }

    private static Task RestoreMySqlAsync(ConnectionProfile profile, string database, string path)
    {
        var cs = new MySqlConnectionStringBuilder(profile.BuildConnectionString());
        var tools = profile.Engine == DatabaseEngine.MariaDb
            ? new[] { "mariadb", "mysql" }
            : new[] { "mysql", "mariadb" };
        return RunExternalAsync(tools,
            ["--host", cs.Server, "--port", cs.Port.ToString(), "--user", cs.UserID,
             "--database", database],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = cs.Password }, path);
    }

    private static Task BackupMongoAsync(ConnectionProfile profile, string database, string path) =>
        RunMongoAsync("mongodump", profile,
            ["--db", database, $"--archive={path}", "--gzip"]);

    private static Task RestoreMongoAsync(ConnectionProfile profile, string database, string path) =>
        RunMongoAsync("mongorestore", profile,
            [$"--nsInclude={database}.*", $"--archive={path}", "--gzip", "--drop"]);

    private static Task BackupFirebirdAsync(ConnectionProfile profile, string path) =>
        RunExternalAsync(["gbak"],
            ["-b", "-g", FirebirdDatabaseArgument(profile), path],
            FirebirdEnvironment(profile));

    private static Task RestoreFirebirdAsync(ConnectionProfile profile, string path) =>
        RunExternalAsync(["gbak"],
            ["-c", "-rep", path, FirebirdDatabaseArgument(profile)],
            FirebirdEnvironment(profile));

    private static IReadOnlyDictionary<string, string?> FirebirdEnvironment(ConnectionProfile profile) =>
        new Dictionary<string, string?>
        {
            ["ISC_USER"] = profile.Username ?? "SYSDBA",
            ["ISC_PASSWORD"] = profile.Password ?? "masterkey"
        };

    private static string FirebirdDatabaseArgument(ConnectionProfile profile)
    {
        if (profile.FirebirdEmbedded) return profile.FilePath ?? "";
        var host = string.IsNullOrWhiteSpace(profile.Server) ? "localhost" : profile.Server;
        var port = profile.Port > 0 ? profile.Port : 3050;
        return $"{host}/{port}:{profile.FilePath}";
    }

    private static async Task RunMongoAsync(
        string tool, ConnectionProfile profile, IReadOnlyList<string> arguments)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dataportstudio_{Guid.NewGuid():N}.yml");
        var uri = profile.BuildConnectionString().Replace("'", "''");
        try
        {
            await File.WriteAllTextAsync(tempFile, $"uri: '{uri}'{Environment.NewLine}");
            var protectedArguments = new List<string> { $"--config={tempFile}" };
            protectedArguments.AddRange(arguments);
            await RunExternalAsync([tool], protectedArguments);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static Task BackupOracleAsync(ConnectionProfile profile, string path) =>
        RunOracleAsync("exp", profile, path, restore: false);

    private static Task RestoreOracleAsync(ConnectionProfile profile, string path) =>
        RunOracleAsync("imp", profile, path, restore: true);

    private static async Task RunOracleAsync(
        string tool, ConnectionProfile profile, string path, bool restore)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dataportstudio_{Guid.NewGuid():N}.par");
        var host = string.IsNullOrWhiteSpace(profile.Server) ? "localhost" : profile.Server;
        var port = profile.Port > 0 ? profile.Port : 1521;
        var user = profile.Username ?? "";
        var login = $"{user}/{profile.Password}@{host}:{port}/{profile.Database}";
        var lines = new List<string>
        {
            $"userid=\"{login.Replace("\"", "\"\"")}\"",
            $"file=\"{path.Replace("\"", "\"\"")}\"",
            "log=\"NUL\""
        };
        if (restore)
        {
            lines.Add($"fromuser=\"{user}\"");
            lines.Add($"touser=\"{user}\"");
            lines.Add("ignore=y");
        }
        else
        {
            lines.Add($"owner=\"{user}\"");
            lines.Add("rows=y");
            lines.Add("indexes=y");
            lines.Add("grants=y");
        }

        try
        {
            await File.WriteAllLinesAsync(tempFile, lines);
            await RunExternalAsync([tool], [$"parfile={tempFile}"]);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static async Task RunExternalAsync(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? standardInputFile = null)
    {
        Process? process = null;
        foreach (var candidate in candidates)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInputFile is not null
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            if (environment is not null)
                foreach (var pair in environment)
                    if (pair.Value is not null) startInfo.Environment[pair.Key] = pair.Value;

            try
            {
                process = Process.Start(startInfo);
                if (process is not null) break;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
            {
                process = null;
            }
        }

        if (process is null)
            throw new InvalidOperationException(string.Format(
                LocalizationManager.Instance["DbBackup_ToolMissing"], string.Join(" / ", candidates)));

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (standardInputFile is not null)
            {
                await using var input = File.OpenRead(standardInputFile);
                await input.CopyToAsync(process.StandardInput.BaseStream);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync();
            var outputText = await stdout;
            var errorText = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(errorText) ? outputText.Trim() : errorText.Trim());
        }
    }
}
