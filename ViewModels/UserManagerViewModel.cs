using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPortStudio.Models;
using DataPortStudio.Services.Security;
using DataPortStudio.Views;

namespace DataPortStudio.ViewModels;

/// <summary>A role shown in the "Member of" list, with its current and pending membership state.</summary>
public sealed partial class RoleMembership : ObservableObject
{
    public required string Role { get; init; }
    [ObservableProperty] private bool isMember;
    public bool OriginalIsMember { get; set; }
    public bool IsDirty => IsMember != OriginalIsMember;
}

public sealed partial class UserManagerViewModel : ObservableObject
{
    private readonly SecurityProvider _provider;

    public string ConnectionName { get; }
    public SecurityCapabilities Caps => _provider.Capabilities;

    public ObservableCollection<SecurityPrincipal> Principals { get; } = new();
    public ObservableCollection<RoleMembership> Memberships { get; } = new();
    public ObservableCollection<PrivilegeItem> Privileges { get; } = new();
    public ObservableCollection<string> Scopes { get; } = new();

    [ObservableProperty] private SecurityPrincipal? selectedPrincipal;
    [ObservableProperty] private string? selectedScope;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool busy;

    // Inline create panel state.
    [ObservableProperty] private bool isCreating;
    [ObservableProperty] private bool createIsRole;
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private string newHost = "%";
    [ObservableProperty] private bool privilegesEmpty = true;

    public bool HasSelection => SelectedPrincipal is not null;
    public bool CanEditSelection => SelectedPrincipal is { IsBuiltIn: false };
    public string CreateTitle => CreateIsRole ? "New role" : $"New {Caps.UserNoun.ToLowerInvariant()}";

    public bool ShowCreateHost => Caps.HasHost && !CreateIsRole;
    public bool ShowCreatePassword => Caps.SupportsPassword && !CreateIsRole;
    public bool MembershipVisible => Caps.SupportsRoles;
    public bool ShowSetPassword => CanEditSelection && Caps.SupportsPassword && SelectedPrincipal is { IsRole: false };
    public bool ShowLockButton => Caps.SupportsLock && CanEditSelection && SelectedPrincipal is { IsRole: false };
    public string SelectionSummary => SelectedPrincipal is { } p
        ? $"{(p.IsRole ? "Role" : Caps.UserNoun)} · {p.Display}{(p.Locked ? " · locked" : "")}{(p.IsBuiltIn ? " · built-in" : "")}"
        : "";

    public UserManagerViewModel(ConnectionProfile connection)
    {
        ConnectionName = connection.Name;
        _provider = SecurityProvider.For(connection)
            ?? throw new InvalidOperationException("This engine has no user manager.");
        _ = LoadAsync();
    }

    private async Task RunAsync(string working, Func<Task> action, string? done = null)
    {
        if (Busy) return;
        Busy = true;
        StatusText = working;
        try
        {
            await action();
            if (done is not null) StatusText = done;
        }
        catch (Exception ex)
        {
            StatusText = "Failed.";
            Dialogs.ShowError("Operation failed", ex.Message);
        }
        finally { Busy = false; }
    }

    private async Task LoadAsync()
    {
        await RunAsync("Loading…", async () =>
        {
            var keepName = SelectedPrincipal?.Name;
            var keepHost = SelectedPrincipal?.Host;

            var principals = await _provider.GetPrincipalsAsync();
            Principals.Clear();
            foreach (var p in principals.OrderByDescending(p => !p.IsRole).ThenBy(p => p.Display, StringComparer.OrdinalIgnoreCase))
                Principals.Add(p);

            Scopes.Clear();
            Scopes.Add(Caps.GlobalScopeLabel);
            if (Caps.SupportsDatabaseScope)
                foreach (var db in await _provider.GetDatabasesAsync()) Scopes.Add(db);
            SelectedScope = Scopes.FirstOrDefault();

            SelectedPrincipal = Principals.FirstOrDefault(p => p.Name == keepName && p.Host == keepHost)
                                ?? Principals.FirstOrDefault();
        }, $"{Principals.Count} principal(s).");
    }

    partial void OnSelectedPrincipalChanged(SecurityPrincipal? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(ShowSetPassword));
        OnPropertyChanged(nameof(ShowLockButton));
        OnPropertyChanged(nameof(SelectionSummary));
        _ = ReloadDetailAsync();
    }

    partial void OnSelectedScopeChanged(string? value) => _ = ReloadPrivilegesAsync();

    partial void OnCreateIsRoleChanged(bool value)
    {
        OnPropertyChanged(nameof(CreateTitle));
        OnPropertyChanged(nameof(ShowCreateHost));
        OnPropertyChanged(nameof(ShowCreatePassword));
    }

    private async Task ReloadDetailAsync()
    {
        await ReloadMembershipsAsync();
        await ReloadPrivilegesAsync();
    }

    private async Task ReloadMembershipsAsync()
    {
        Memberships.Clear();
        var p = SelectedPrincipal;
        if (p is null || !Caps.SupportsRoles) return;
        await RunAsync("Loading roles…", async () =>
        {
            var member = await _provider.GetMembershipsAsync(p);
            var roles = Principals.Where(x => x.IsRole).Select(x => x.Name)
                                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            Memberships.Clear();
            foreach (var role in roles)
            {
                var on = member.Contains(role);
                Memberships.Add(new RoleMembership { Role = role, IsMember = on, OriginalIsMember = on });
            }
        });
    }

    private async Task ReloadPrivilegesAsync()
    {
        Privileges.Clear();
        PrivilegesEmpty = true;
        var p = SelectedPrincipal;
        if (p is null || SelectedScope is null) return;
        var (scope, db) = ResolveScope();
        if (scope == PrivilegeScopeKind.Global && Caps.GlobalPrivileges.Count == 0) return;
        await RunAsync("Loading privileges…", async () =>
        {
            var items = await _provider.GetPrivilegesAsync(p, scope, db);
            Privileges.Clear();
            foreach (var i in items) Privileges.Add(i);
        });
        PrivilegesEmpty = Privileges.Count == 0;
    }

    private (PrivilegeScopeKind, string?) ResolveScope() =>
        SelectedScope == Caps.GlobalScopeLabel || SelectedScope is null
            ? (PrivilegeScopeKind.Global, null)
            : (PrivilegeScopeKind.Database, SelectedScope);

    [RelayCommand] private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void NewUser()
    {
        CreateIsRole = false;
        NewName = "";
        NewHost = "%";
        IsCreating = true;
    }

    [RelayCommand]
    private void NewRole()
    {
        CreateIsRole = true;
        NewName = "";
        IsCreating = true;
    }

    [RelayCommand] private void CancelCreate() => IsCreating = false;

    /// <summary>Called from the window (it owns the PasswordBox for the create form).</summary>
    public async Task ConfirmCreateAsync(string? password)
    {
        var name = NewName.Trim();
        if (string.IsNullOrEmpty(name)) { Dialogs.ShowError("Missing name", "Enter a name."); return; }
        await RunAsync("Creating…", async () =>
        {
            if (CreateIsRole) await _provider.CreateRoleAsync(name);
            else await _provider.CreateUserAsync(name, Caps.HasHost ? NewHost.Trim() : null, password);
            IsCreating = false;
            await LoadAsync();
            SelectedPrincipal = Principals.FirstOrDefault(p => p.Name == name);
        }, "Created.");
    }

    [RelayCommand]
    private async Task Delete()
    {
        var p = SelectedPrincipal;
        if (p is null) return;
        if (p.IsBuiltIn) { Dialogs.ShowError("Protected", $"'{p.Display}' is a built-in principal and can't be dropped."); return; }
        var noun = p.IsRole ? "role" : Caps.UserNoun.ToLowerInvariant();
        if (!Dialogs.ConfirmDanger("Drop " + noun, $"Drop {noun} '{p.Display}'? This cannot be undone.", "Drop"))
            return;
        await RunAsync("Dropping…", async () =>
        {
            await _provider.DropAsync(p);
            await LoadAsync();
        }, "Dropped.");
    }

    /// <summary>Sets/changes the selected principal's password (window owns the PasswordBox).</summary>
    public async Task SetPasswordAsync(string password)
    {
        var p = SelectedPrincipal;
        if (p is null) return;
        if (string.IsNullOrEmpty(password)) { Dialogs.ShowError("Missing password", "Enter a password."); return; }
        await RunAsync("Setting password…", () => _provider.SetPasswordAsync(p, password), "Password updated.");
    }

    [RelayCommand]
    private async Task ToggleLock()
    {
        var p = SelectedPrincipal;
        if (p is null || !Caps.SupportsLock) return;
        await RunAsync(p.Locked ? "Unlocking…" : "Locking…", async () =>
        {
            await _provider.SetLockedAsync(p, !p.Locked);
            await LoadAsync();
        }, p.Locked ? "Unlocked." : "Locked.");
    }

    [RelayCommand]
    private async Task Save()
    {
        var p = SelectedPrincipal;
        if (p is null) return;
        await RunAsync("Saving…", async () =>
        {
            foreach (var m in Memberships.Where(m => m.IsDirty))
            {
                if (m.IsMember) await _provider.GrantRoleAsync(m.Role, p);
                else await _provider.RevokeRoleAsync(m.Role, p);
                m.OriginalIsMember = m.IsMember;
            }

            if (Privileges.Any(i => i.IsDirty))
            {
                var (scope, db) = ResolveScope();
                await _provider.ApplyPrivilegesAsync(p, scope, db, Privileges);
                foreach (var i in Privileges) i.OriginalGranted = i.Granted;
            }
        }, "Saved.");
    }

    [RelayCommand]
    private void GrantAll() { foreach (var i in Privileges) i.Granted = true; }

    [RelayCommand]
    private void RevokeAll() { foreach (var i in Privileges) i.Granted = false; }
}
