using SmartPrint.Api.Domain;
namespace SmartPrint.Api.Tests;
public sealed class PlatformAdvertisingRulesTests
{
    [Theory]
    [InlineData("HOME","BANNER",true)]
    [InlineData("SHOP_OVERVIEW","BANNER",true)]
    [InlineData("FILES","CARD",true)]
    [InlineData("HOME","CARD",false)]
    [InlineData("FILES","BANNER",false)]
    [InlineData("CHECKOUT","BANNER",false)]
    public void Placement_format_pair_is_limited_to_approved_slots(string placement,string format,bool expected) =>
        Assert.Equal(expected,PlatformAdvertisingRules.IsValidPlacementFormat(placement,format));

    [Theory]
    [InlineData("https://advertiser.example/offer",true)]
    [InlineData("http://advertiser.example/offer",false)]
    [InlineData("javascript:alert(1)",false)]
    [InlineData("https://user:password@example.com",false)]
    [InlineData("https://localhost/path",false)]
    [InlineData("https://127.0.0.1/path",false)]
    [InlineData("https://192.168.1.10/path",false)]
    [InlineData("https://advertiser.example:8443/path",false)]
    public void Destination_must_be_public_https(string url,bool expected) =>
        Assert.Equal(expected,PlatformAdvertisingRules.IsSafeDestinationUrl(url));

    [Fact]
    public void Campaign_is_deliverable_only_when_active_and_inside_window()
    {
        var now=DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var active=new PlatformAdCampaign{Status="Active",StartsUtc=now.AddHours(-1),EndsUtc=now.AddHours(1)};
        var paused=new PlatformAdCampaign{Status="Paused",StartsUtc=now.AddHours(-1)};
        var scheduled=new PlatformAdCampaign{Status="Active",StartsUtc=now.AddHours(1)};
        Assert.True(PlatformAdvertisingRules.IsActiveNow(active,now));
        Assert.False(PlatformAdvertisingRules.IsActiveNow(paused,now));
        Assert.False(PlatformAdvertisingRules.IsActiveNow(scheduled,now));
        Assert.Equal("Scheduled",PlatformAdvertisingRules.DisplayStatus(scheduled,now));
    }
}
