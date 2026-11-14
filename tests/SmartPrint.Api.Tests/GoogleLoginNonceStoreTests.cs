using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class GoogleLoginNonceStoreTests
{
    [Fact]
    public void IssuesCryptographicallyRandomSurfaceBoundNonceAndConsumesItOnce()
    {
        var store = new GoogleLoginNonceStore();

        var nonce = store.Issue("shop");

        Assert.Matches("^[a-f0-9]{64}$", nonce);
        Assert.True(store.TryConsume("shop", nonce));
        Assert.False(store.TryConsume("shop", nonce));
    }

    [Fact]
    public void NonceCannotBeConsumedForTheOtherAccountSurface()
    {
        var store = new GoogleLoginNonceStore();
        var nonce = store.Issue("customer");

        Assert.False(store.TryConsume("shop", nonce));
        Assert.True(store.TryConsume("customer", nonce));
    }

    [Fact]
    public void RejectsMalformedOrMissingNonces()
    {
        var store = new GoogleLoginNonceStore();

        Assert.False(store.TryConsume("shop", null));
        Assert.False(store.TryConsume("shop", "short"));
        Assert.False(store.TryConsume("admin", new string('a', 64)));
    }
}
