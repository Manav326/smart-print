namespace SmartPrint.Documents;

/// <summary>
/// Shared hard safety limits for raster images accepted by upload validation and
/// consumed by the Windows print renderer. Keep both stages on the same contract.
/// </summary>
public static class DocumentImageLimits
{
    public const uint MaximumDimension = 10_000;
    public const ulong MaximumPixels = 24_000_000UL;
}
