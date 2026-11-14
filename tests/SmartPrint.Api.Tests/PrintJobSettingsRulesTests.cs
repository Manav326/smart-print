using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintJobSettingsRulesTests
{
    [Theory]
    [InlineData(" Mono ", " a4 ", " portrait ", 1)]
    [InlineData("Colour", "A3", "LANDSCAPE", 100)]
    [InlineData("Mono", "LEGAL", "AUTO", 10)]
    public void TryNormalize_trims_and_normalizes_supported_settings(
        string colourMode,
        string paperSize,
        string orientation,
        int copies)
    {
        var valid = PrintJobSettingsRules.TryNormalize(
            colourMode, paperSize, isDuplex: true, copies, orientation, out var settings);

        Assert.True(valid);
        Assert.NotNull(settings);
        Assert.Equal(colourMode.Trim(), settings.ColourMode);
        Assert.Equal(paperSize.Trim().ToUpperInvariant(), settings.PaperSize);
        Assert.Equal(orientation.Trim().ToUpperInvariant(), settings.Orientation);
        Assert.Equal(copies, settings.Copies);
        Assert.True(settings.IsDuplex);
    }

    [Theory]
    [InlineData("mono", "A4", "AUTO", 1)]
    [InlineData("Mono", "A0", "AUTO", 1)]
    [InlineData("Mono", "A4", "SIDEWAYS", 1)]
    [InlineData("Mono", "A4", "AUTO", 0)]
    [InlineData("Mono", "A4", "AUTO", 101)]
    [InlineData(null, "A4", "AUTO", 1)]
    [InlineData("Colour", null, "AUTO", 1)]
    [InlineData("Colour", "A4", null, 1)]
    public void TryNormalize_rejects_unsupported_settings(
        string? colourMode,
        string? paperSize,
        string? orientation,
        int copies)
    {
        Assert.False(PrintJobSettingsRules.TryNormalize(
            colourMode, paperSize, isDuplex: false, copies, orientation, out _));
    }

    [Theory]
    [InlineData("DOCUMENT", "AWAITING_CONFIGURATION", true)]
    [InlineData("DOCUMENT", "AWAITING_CUSTOMER_APPROVAL", true)]
    [InlineData("DOCUMENT", "AWAITING_SHOP_APPROVAL", true)]
    [InlineData("DOCUMENT", "AWAITING_PAYMENT", true)]
    [InlineData("DOCUMENT", "QUOTE_EXPIRED", true)]
    [InlineData("DOCUMENT", "PAYMENT_REJECTED", true)]
    [InlineData("PHOTO", "AWAITING_CONFIGURATION", false)]
    [InlineData("DOCUMENT", "LOCAL_QUEUE_READY", false)]
    [InlineData("DOCUMENT", "COMPLETED", false)]
    [InlineData(null, "AWAITING_PAYMENT", false)]
    public void CanRevise_only_allows_document_jobs_before_agent_handoff(
        string? printKind,
        string? status,
        bool expected) =>
        Assert.Equal(expected, PrintJobSettingsRules.CanRevise(printKind, status));
}
