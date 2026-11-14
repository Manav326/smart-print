using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class OpaqueTokenHasherTests
{
    [Fact]
    public void ProducesTheSameLowercaseSha256DigestForTheSameToken()
    {
        var first = OpaqueTokenHasher.HashOpaqueToken("single-use-token");
        var second = OpaqueTokenHasher.HashOpaqueToken("single-use-token");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, character => Assert.True(char.IsAsciiHexDigit(character) && !char.IsUpper(character)));
    }

    [Fact]
    public void DifferentTokensProduceDifferentDigests() =>
        Assert.NotEqual(OpaqueTokenHasher.HashOpaqueToken("first-token"), OpaqueTokenHasher.HashOpaqueToken("second-token"));
}
