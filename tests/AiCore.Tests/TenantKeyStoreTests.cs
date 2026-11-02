using AiCore.Api.Authentication;
using AiCore.Api.Configuration;

namespace AiCore.Tests;

public sealed class TenantKeyStoreTests
{
    [Fact]
    public void ResolvesTenantByExactKey()
    {
        var store = new TenantKeyStore([new TenantApiKeyOptions { TenantId = "t1", ApiKey = "k1" }]);

        Assert.True(store.TryResolveTenant("k1", out var tenant));
        Assert.Equal("t1", tenant);
        Assert.False(store.TryResolveTenant("K1", out _));
        Assert.False(store.TryResolveTenant("k1 ", out _));
    }

    [Fact]
    public void DuplicateKey_IsRejected_WithoutRevealingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new TenantKeyStore(
        [
            new TenantApiKeyOptions { TenantId = "t1", ApiKey = "shared-secret" },
            new TenantApiKeyOptions { TenantId = "t2", ApiKey = "shared-secret" },
        ]));

        Assert.DoesNotContain("shared-secret", ex.Message);
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("tenant", "")]
    public void IncompleteEntry_IsRejected(string tenantId, string apiKey)
    {
        Assert.Throws<InvalidOperationException>(
            () => new TenantKeyStore([new TenantApiKeyOptions { TenantId = tenantId, ApiKey = apiKey }]));
    }
}
