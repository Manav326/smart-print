using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformOperationalSettingsPolicyTests
{
    [Fact]
    public void StandardCompletedDocumentsDefaultToSeventyTwoHoursWithHourlyCleanup()
    {
        var settings = new PlatformOperationalSettings();

        Assert.Equal(3, settings.CloudCompletedDocumentRetentionDays);
        Assert.Equal(3, settings.AgentCompletedDocumentRetentionDays);
        Assert.Equal(1, settings.ApiRetentionSweepHours);
        Assert.Equal(1, settings.AgentRetentionSweepHours);
        Assert.False(settings.AllowPrivatePrintingVirtualOutput);
    }
}
