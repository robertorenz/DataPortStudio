using DataPortStudio.Models;

namespace DataPortStudio.Services.Security;

/// <summary>
/// Engine-agnostic surface for managing logins / users / roles and their privileges.
/// One concrete provider per relational engine; <see cref="For"/> builds the right one.
/// </summary>
public abstract class SecurityProvider
{
    protected readonly ConnectionProfile Profile;
    protected string Cs => Profile.BuildConnectionString();

    protected SecurityProvider(ConnectionProfile profile) => Profile = profile;

    public abstract SecurityCapabilities Capabilities { get; }

    /// <summary>All users and roles on the server (and, for SQL Server, the database).</summary>
    public abstract Task<List<SecurityPrincipal>> GetPrincipalsAsync();

    /// <summary>Databases the privilege editor can target (empty when scoping isn't supported).</summary>
    public abstract Task<List<string>> GetDatabasesAsync();

    /// <summary>Role names the principal is currently a member of.</summary>
    public abstract Task<HashSet<string>> GetMembershipsAsync(SecurityPrincipal p);

    public abstract Task GrantRoleAsync(string role, SecurityPrincipal p);
    public abstract Task RevokeRoleAsync(string role, SecurityPrincipal p);

    public abstract Task CreateUserAsync(string name, string? host, string? password);
    public abstract Task CreateRoleAsync(string name);
    public abstract Task DropAsync(SecurityPrincipal p);
    public abstract Task SetPasswordAsync(SecurityPrincipal p, string password);
    public abstract Task SetLockedAsync(SecurityPrincipal p, bool locked);

    /// <summary>Reads the privilege grid for a principal at a scope (with Granted/OriginalGranted set).</summary>
    public abstract Task<List<PrivilegeItem>> GetPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database);

    /// <summary>Issues GRANT for newly-checked and REVOKE for newly-unchecked items.</summary>
    public abstract Task ApplyPrivilegesAsync(
        SecurityPrincipal p, PrivilegeScopeKind scope, string? database, IReadOnlyList<PrivilegeItem> items);

    /// <summary>Builds privilege rows from a capability keyword list, marking which are granted.</summary>
    protected static List<PrivilegeItem> BuildItems(IEnumerable<string> keywords, ISet<string> granted)
        => keywords.Select(k =>
        {
            var on = granted.Contains(k);
            return new PrivilegeItem { Name = k, Keyword = k, Granted = on, OriginalGranted = on };
        }).ToList();

    /// <summary>True when the engine has any concept of users/roles worth managing.</summary>
    public static bool IsSupported(DatabaseEngine e) =>
        e is DatabaseEngine.SqlServer or DatabaseEngine.MySql or DatabaseEngine.MariaDb or DatabaseEngine.Firebird;

    public static SecurityProvider? For(ConnectionProfile p) => p.Engine switch
    {
        DatabaseEngine.MySql or DatabaseEngine.MariaDb => new MySqlSecurityProvider(p),
        DatabaseEngine.SqlServer => new SqlServerSecurityProvider(p),
        DatabaseEngine.Firebird => new FirebirdSecurityProvider(p),
        _ => null
    };
}
