namespace DataPortStudio.Models;

/// <summary>Engine-specific values collected when creating a server-level database.</summary>
public sealed class DatabaseCreationOptions
{
    public string Name { get; init; } = "";
    public string? SqlServerCollation { get; init; }
    public string SqlServerRecoveryModel { get; init; } = "FULL";
    public int SqlServerCompatibilityLevel { get; init; } = 160;
    public string? SqlServerDataLogicalName { get; init; }
    public string? SqlServerDataFilePath { get; init; }
    public int SqlServerDataInitialSizeMb { get; init; } = 8;
    public int? SqlServerDataMaxSizeMb { get; init; }
    public int SqlServerDataGrowthMb { get; init; } = 64;
    public string? SqlServerLogLogicalName { get; init; }
    public string? SqlServerLogFilePath { get; init; }
    public int SqlServerLogInitialSizeMb { get; init; } = 8;
    public int? SqlServerLogMaxSizeMb { get; init; }
    public int SqlServerLogGrowthMb { get; init; } = 64;
    public string? PostgresOwner { get; init; }
    public string PostgresEncoding { get; init; } = "UTF8";
    public string MySqlCharacterSet { get; init; } = "utf8mb4";
    public string? MySqlCollation { get; init; }
    public string? MongoInitialCollection { get; init; }
}
