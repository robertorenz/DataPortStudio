namespace DataPortStudio.Models;

/// <summary>The database engine a connection talks to.</summary>
public enum DatabaseEngine
{
    SqlServer,
    Sqlite,
    PostgreSql,
    MongoDb,
    Firebird,
    MySql,
    MariaDb,
    Tps,
    ClarionDat,
    Oracle,
    Excel
}

public static class DatabaseEngineInfo
{
    /// <summary>Short, human-friendly engine name for the UI.</summary>
    public static string DisplayName(this DatabaseEngine e) => e switch
    {
        DatabaseEngine.SqlServer => "SQL Server",
        DatabaseEngine.Sqlite => "SQLite",
        DatabaseEngine.PostgreSql => "PostgreSQL",
        DatabaseEngine.MongoDb => "MongoDB",
        DatabaseEngine.Firebird => "Firebird",
        DatabaseEngine.MySql => "MySQL",
        DatabaseEngine.MariaDb => "MariaDB",
        DatabaseEngine.Tps => "TPS (Clarion)",
        DatabaseEngine.ClarionDat => "Clarion DAT",
        DatabaseEngine.Oracle => "Oracle",
        DatabaseEngine.Excel => "Excel",
        _ => e.ToString()
    };

    /// <summary>MySQL and MariaDB share the same driver and SQL.</summary>
    public static bool IsMySql(this DatabaseEngine e) =>
        e is DatabaseEngine.MySql or DatabaseEngine.MariaDb;

    /// <summary>True for engines that are fully implemented today.</summary>
    public static bool IsSupported(this DatabaseEngine e) =>
        e is DatabaseEngine.SqlServer or DatabaseEngine.Sqlite
            or DatabaseEngine.Firebird or DatabaseEngine.MongoDb
            or DatabaseEngine.MySql or DatabaseEngine.MariaDb
            or DatabaseEngine.Tps or DatabaseEngine.ClarionDat
            or DatabaseEngine.Oracle or DatabaseEngine.Excel;

    /// <summary>Read-only engines: browse and copy out, but no editing, designing or writing back.</summary>
    public static bool IsReadOnly(this DatabaseEngine e) =>
        e is DatabaseEngine.MongoDb or DatabaseEngine.ClarionDat or DatabaseEngine.Excel;

    /// <summary>Engines that support cell edits but not INSERT or DELETE (fixed binary format).</summary>
    public static bool IsEditOnly(this DatabaseEngine e) =>
        e is DatabaseEngine.Tps;

    /// <summary>Clarion flat-file engines: a connection is a folder and each file is a table.</summary>
    public static bool IsClarionFile(this DatabaseEngine e) =>
        e is DatabaseEngine.Tps or DatabaseEngine.ClarionDat;
}
