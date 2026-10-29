using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Webhooks;

namespace OpenBook.Domain.Tests;

public class MultiUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Roles_include_the_ones_below_and_changing_one_ends_sessions()
    {
        var user = User.Create("Clerk", "hash", Now, UserRole.Standard);
        Assert.True(user.Can(UserRole.ViewOnly));
        Assert.True(user.Can(UserRole.Standard));
        Assert.False(user.Can(UserRole.Accountant));

        var stamp = user.SecurityStamp;
        user.ChangeRole(UserRole.Standard);
        Assert.Equal(stamp, user.SecurityStamp);
        user.ChangeRole(UserRole.Accountant);
        Assert.NotEqual(stamp, user.SecurityStamp);

        stamp = user.SecurityStamp;
        user.Deactivate();
        Assert.False(user.Can(UserRole.ViewOnly));
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.Equal(UserRole.Admin, User.Create("owner", "hash", Now).Role);
    }

    [Fact]
    public void Api_keys_store_only_a_hash_and_stop_when_revoked_or_expired()
    {
        var (key, token) = ApiKey.Create(Guid.NewGuid(), " Shop sync ", Now, Now.AddDays(30));
        Assert.StartsWith(ApiKey.TokenPrefix, token, StringComparison.Ordinal);
        Assert.True(token.Length > 40);
        Assert.Equal(ApiKey.HashToken(token), key.TokenHash);
        Assert.DoesNotContain(token, key.TokenHash, StringComparison.Ordinal);
        Assert.Equal("Shop sync", key.Name);
        Assert.Equal(token[..10] + "…", key.Hint);

        Assert.True(key.IsUsable(Now));
        Assert.False(key.IsUsable(Now.AddDays(31)));
        Assert.True(key.RecordUse(Now));
        Assert.False(key.RecordUse(Now.AddSeconds(30)));
        Assert.True(key.RecordUse(Now.AddMinutes(2)));

        key.Revoke(Now);
        Assert.False(key.IsUsable(Now));
        Assert.Equal("apikey.expiry_past", Assert.Throws<DomainException>(() => ApiKey.Create(Guid.NewGuid(), "x", Now, Now.AddDays(-1))).Code);
        Assert.Equal("apikey.name_required", Assert.Throws<DomainException>(() => ApiKey.Create(Guid.NewGuid(), " ", Now)).Code);
    }

    [Theory]
    [InlineData("*", "invoice.created", true)]
    [InlineData("invoice.*", "invoice.voided", true)]
    [InlineData("invoice.*", "bill.voided", false)]
    [InlineData("*.voided", "bill.voided", true)]
    [InlineData("*.voided", "bill.created", false)]
    [InlineData("Payment.Created, bill.*", "payment.created", true)]
    [InlineData("", "anything.at_all", true)]
    public void Webhook_event_patterns_match(string events, string type, bool matches)
    {
        var subscription = WebhookSubscription.Create("https://example.com/hook", events, null, "secret", 0, Now);
        Assert.Equal(matches, subscription.Matches(type));
    }

    [Theory]
    [InlineData("ftp://example.com", "webhook.url_invalid")]
    [InlineData("/relative", "webhook.url_invalid")]
    [InlineData("https://user:pw@example.com/hook", "webhook.url_credentials")]
    public void Webhook_addresses_must_be_plain_http_urls(string url, string code)
    {
        Assert.Equal(code, Assert.Throws<DomainException>(() => WebhookSubscription.Create(url, null, null, "s", 0, Now)).Code);
        Assert.Equal("webhook.event_invalid", Assert.Throws<DomainException>(() => WebhookSubscription.NormalizeEvents("invoice")).Code);
        Assert.Equal("webhook.event_invalid", Assert.Throws<DomainException>(() => WebhookSubscription.NormalizeEvents("invoice.created.now")).Code);
    }

    [Fact]
    public void Deliveries_back_off_then_give_up_and_can_be_sent_again()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "invoice.created", 7, "{}", Now);
        delivery.RecordAttempt(Now, 500, null);
        Assert.Equal((WebhookDeliveryStatus.Pending, Now.AddMinutes(1), "HTTP 500"), (delivery.Status, delivery.NextAttemptAt, delivery.LastError));
        delivery.RecordAttempt(Now.AddMinutes(1), null, "Connection refused");
        Assert.Equal(Now.AddMinutes(6), delivery.NextAttemptAt);

        for (var i = 2; i < WebhookDelivery.MaxAttempts; i++)
            delivery.RecordAttempt(Now.AddDays(i), 503, null);
        Assert.Equal(WebhookDeliveryStatus.Failed, delivery.Status);

        delivery.Redeliver(Now.AddDays(10));
        Assert.Equal((WebhookDeliveryStatus.Pending, 0), (delivery.Status, delivery.Attempts));
        delivery.RecordAttempt(Now.AddDays(10), 204, null);
        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Null(delivery.LastError);
    }
}

public class ApiScopeTests
{
    [Fact]
    public void Scopes_are_normalised_and_checked_by_path()
    {
        Assert.Equal(["reports:read", "sales:write"], OpenBook.Domain.Companies.ApiScopes.Normalize(["sales:read", "sales:write", " reports:read "]));
        Assert.Equal("apikey.scope_invalid", Assert.Throws<OpenBook.Domain.Common.DomainException>(() => OpenBook.Domain.Companies.ApiScopes.Normalize(["sales:admin"])).Code);
        Assert.Equal("apikey.scope_required", Assert.Throws<OpenBook.Domain.Common.DomainException>(() => OpenBook.Domain.Companies.ApiScopes.Normalize([])).Code);

        string[] scopes = ["sales:read", "projects:write"];
        Assert.True(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/sales/invoices", write: false));
        Assert.False(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/sales/invoices", write: true));
        Assert.True(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/projects/time", write: true));
        Assert.False(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/salesfoo", write: false));
        Assert.False(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/webhooks", write: false));
        Assert.True(OpenBook.Domain.Companies.ApiScopes.Allows(scopes, "/api/me", write: false));
    }
}
