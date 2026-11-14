using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class GoogleAuthNonceStoreTests
{
    [Fact]
    public void NonceCanBeConsumedExactlyOnce()
    {
        var store = new GoogleAuthNonceStore();
        var nonce = store.Issue();

        Assert.True(store.TryConsume(nonce, nonce));
        Assert.False(store.TryConsume(nonce, nonce));
    }

    [Fact]
    public void MismatchedExpectedNonceDoesNotConsumeTheIssuedChallenge()
    {
        var store = new GoogleAuthNonceStore();
        var nonce = store.Issue();

        Assert.False(store.TryConsume(nonce, "different-challenge"));
        Assert.True(store.TryConsume(nonce, nonce));
    }

    [Fact]
    public void MissingNonceIsRejected()
    {
        var store = new GoogleAuthNonceStore();
        Assert.False(store.TryConsume(null, "expected"));
        Assert.False(store.TryConsume("signed", null));
    }
}
