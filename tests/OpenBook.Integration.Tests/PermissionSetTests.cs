using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

public sealed class PermissionSetTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private static Dictionary<string, UserRole> Clerk() => new() { ["sales"] = UserRole.Standard, ["reports"] = UserRole.ViewOnly };

    [Fact]
    public async Task A_user_with_a_set_signs_in_with_its_levels()
    {
        var set = await Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("Sales clerk", "Front desk", Clerk()));
        Assert.Equal("sales:Standard,reports:ViewOnly", set.Levels);
        var pat = await Run(sp => sp.GetRequiredService<UserService>().CreateAsync("pat", "a long password", UserRole.ViewOnly));
        await Run(sp => sp.GetRequiredService<UserService>().AssignPermissionSetAsync(pat.Id, set.Id));

        var signedIn = await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "a long password"));
        Assert.Equal(UserRole.Standard, signedIn.User!.Role);
        Assert.Equal(Clerk(), signedIn.User.Areas);
        var key = await Run(sp => sp.GetRequiredService<ApiKeyService>().CreateAsync(pat.Id, "script"));
        Assert.Equal(Clerk(), (await Run(sp => sp.GetRequiredService<ApiKeyService>().AuthenticateAsync(key.Token)))!.Areas);

        // A built-in role takes the set away.
        await Run(sp => sp.GetRequiredService<UserService>().ChangeRoleAsync(pat.Id, UserRole.Accountant));
        var back = await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id));
        Assert.Null(back.PermissionSetId);
        Assert.Null((await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "a long password"))).User!.Areas);
    }

    [Fact]
    public async Task Changing_a_set_signs_its_users_out_and_its_name_is_unique()
    {
        var set = await Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("Sales clerk", null, Clerk()));
        var pat = await Run(sp => sp.GetRequiredService<UserService>().CreateAsync("pat", "a long password", UserRole.ViewOnly));
        pat = await Run(sp => sp.GetRequiredService<UserService>().AssignPermissionSetAsync(pat.Id, set.Id));

        await Run(sp => sp.GetRequiredService<PermissionSetService>().UpdateAsync(set.Id, "Sales clerk", null,
            new Dictionary<string, UserRole> { ["sales"] = UserRole.Accountant }));
        var after = await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id));
        Assert.NotEqual(pat.SecurityStamp, after.SecurityStamp);
        Assert.Equal(UserRole.Accountant, after.Role);

        Assert.Equal("permission_set.name_taken", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("SALES CLERK", null, Clerk())))).Code);
        Assert.Equal("permission_set.level_invalid", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("Boss", null, new Dictionary<string, UserRole> { ["sales"] = UserRole.Admin })))).Code);
        Assert.Equal("permission_set.area_invalid", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("Odd", null, new Dictionary<string, UserRole> { ["payroll"] = UserRole.ViewOnly })))).Code);
        Assert.Equal("permission_set.in_use", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<PermissionSetService>().SetActiveAsync(set.Id, false)))).Code);

        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == PermissionSetService.EntityType).CountAsync());
        Assert.Equal(2, audit);
    }

    [Fact]
    public async Task An_admin_can_give_another_admin_a_set_but_not_themselves()
    {
        var set = await Run(sp => sp.GetRequiredService<PermissionSetService>().CreateAsync("Books", null, new Dictionary<string, UserRole> { ["ledger"] = UserRole.Accountant }));
        var other = await Run(sp => sp.GetRequiredService<UserService>().CreateAsync("second", "a long password", UserRole.Admin));
        await Run(sp => sp.GetRequiredService<UserService>().AssignPermissionSetAsync(other.Id, set.Id));
        Assert.Equal("user.self", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<UserService>().AssignPermissionSetAsync(_db.Owner!.Id, set.Id)))).Code);
    }
}
