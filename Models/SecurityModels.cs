using CommunityToolkit.Mvvm.ComponentModel;

namespace DataPortStudio.Models;

/// <summary>A server login / database user / role, as shown in the User Manager.</summary>
public sealed class SecurityPrincipal
{
    public required string Name { get; init; }
    /// <summary>MySQL host part (<c>user@host</c>); null for engines without per-host accounts.</summary>
    public string? Host { get; init; }
    public bool IsRole { get; init; }
    public bool CanLogin { get; init; } = true;
    public bool Locked { get; init; }
    public bool IsBuiltIn { get; init; }
    public string? Comment { get; init; }

    /// <summary>Engine-specific identity used in GRANT/ALTER statements (e.g. <c>'bob'@'%'</c>).</summary>
    public string Identity => Host is null ? Name : $"{Name}@{Host}";
    public string Display => Host is null ? Name : $"{Name}@{Host}";
    public string Glyph => IsRole ? "R" : "U";
}

/// <summary>One privilege checkbox in the privilege grid.</summary>
public sealed partial class PrivilegeItem : ObservableObject
{
    public required string Name { get; init; }        // shown in the UI, e.g. "SELECT"
    public required string Keyword { get; init; }     // GRANT keyword, e.g. "SELECT"
    [ObservableProperty] private bool granted;
    [ObservableProperty] private bool originalGranted;

    public bool IsDirty => Granted != OriginalGranted;
}

public enum PrivilegeScopeKind { Global, Database }

/// <summary>What a given engine's security model supports — drives the User Manager UI.</summary>
public sealed record SecurityCapabilities
{
    /// <summary>MySQL accounts are <c>user@host</c>.</summary>
    public bool HasHost { get; init; }
    /// <summary>ACCOUNT LOCK / UNLOCK is available.</summary>
    public bool SupportsLock { get; init; }
    /// <summary>Roles can be created/dropped and granted to users.</summary>
    public bool SupportsRoles { get; init; }
    /// <summary>Privileges can be scoped to a specific database (vs only global/server).</summary>
    public bool SupportsDatabaseScope { get; init; }
    /// <summary>A password can be set when creating / for existing principals.</summary>
    public bool SupportsPassword { get; init; } = true;
    /// <summary>Label for the global/server scope tab (e.g. "Global", "Server").</summary>
    public string GlobalScopeLabel { get; init; } = "Global";
    /// <summary>Human noun for a login-capable principal (e.g. "User", "Login").</summary>
    public string UserNoun { get; init; } = "User";
    /// <summary>Note shown at the top of the manager (e.g. scope caveats).</summary>
    public string? Note { get; init; }
    public IReadOnlyList<string> GlobalPrivileges { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DatabasePrivileges { get; init; } = Array.Empty<string>();
}
