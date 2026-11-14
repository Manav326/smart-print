using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantSlugPolicyTests
{
    [Fact]
    public void NormalizesWordsAndSeparators()
    {
        Assert.Equal("quanta-edge-learnings", TenantSlugPolicy.Create("Quanta Edge Learnings"));
    }

    [Fact]
    public void CollapsesRepeatedSeparatorsAndTrimsTheEdges()
    {
        Assert.Equal("quanta-edge", TenantSlugPolicy.Create("  -- Quanta   Edge!!! "));
    }

    [Fact]
    public void UsesTheExistingFallbackWhenNoLettersOrDigitsRemain()
    {
        Assert.Equal("print-shop", TenantSlugPolicy.Create(" --- !!! "));
    }

    [Fact]
    public void KeepsTheExistingSixtyCharacterLimit()
    {
        Assert.Equal(new string('a', 60), TenantSlugPolicy.Create(new string('A', 70)));
    }

    [Fact]
    public void PreservesUnicodeLettersWhileLowercasingInvariantly()
    {
        Assert.Equal("crème-brûlée", TenantSlugPolicy.Create("Crème Brûlée"));
    }
}
