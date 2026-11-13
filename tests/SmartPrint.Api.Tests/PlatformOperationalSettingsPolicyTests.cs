using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformOperationalSettingsPolicyTests
{
    [Fact]
    public void StandardCompletedDocumentsDefaultToSevenDaysWithHourlyCleanup()
    {
        var settings = new PlatformOperationalSettings();

        Assert.Equal(7, settings.CloudCompletedDocumentRetentionDays);
        Assert.Equal(7, settings.AgentCompletedDocumentRetentionDays);
        Assert.Equal(1, settings.ApiRetentionSweepHours);
        Assert.Equal(1, settings.AgentRetentionSweepHours);
        Assert.False(settings.AllowPrivatePrintingVirtualOutput);
    }
}
