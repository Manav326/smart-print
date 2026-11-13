using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Pricing;

public static class PricingCalculator
{
    public static long Calculate(int pageCount, int copies, string colourMode, bool isDuplex, ShopPricingProfile pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        if (pageCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageCount), "Page count must be between 1 and 500.");
        if (copies is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(copies), "Copies must be between 1 and 100.");
        if (colourMode is not ("Mono" or "Colour")) throw new ArgumentException("Colour mode must be Mono or Colour.", nameof(colourMode));
        if (!pricing.IsConfigured) throw new InvalidOperationException("Shop pricing is not configured.");

        var pages = (long)pageCount;
        var copyCount = (long)copies;
        long amount;

        if (isDuplex)
        {
            var rate = colourMode == "Colour" ? pricing.ColourDuplexPerSheetMinor : pricing.MonoDuplexPerSheetMinor;
            if (rate <= 0) throw new DuplexPricingNotConfiguredException();
            var sheets = checked(((pages + 1) / 2) * copyCount);
            amount = checked(sheets * rate);
        }
        else
        {
            var rate = colourMode == "Colour" ? pricing.ColourPerSideMinor : pricing.MonoPerSideMinor;
            if (rate <= 0) throw new InvalidOperationException("The selected print mode is not priced.");
            amount = checked(pages * copyCount * rate);
        }

        if (pricing.MinimumChargeMinor < 0) throw new InvalidOperationException("The configured minimum charge is invalid.");
        return Math.Max(amount, pricing.MinimumChargeMinor);
    }

    public static long CalculatePhoto(int photoCount, string photoSizeCode, ShopPricingProfile pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        if (!pricing.IsConfigured) throw new InvalidOperationException("Shop pricing is not configured.");
        if (photoCount is < 1 or > UploadLimits.MaxPhotoCount) throw new ArgumentOutOfRangeException(nameof(photoCount), $"Photo count must be between 1 and {UploadLimits.MaxPhotoCount}.");
        var unitRate = PhotoUnitRate(photoSizeCode, pricing);
        if (unitRate <= 0) throw new InvalidOperationException("This photo size is not currently priced by the shop.");
        return checked(photoCount * unitRate);
    }

    public static long PhotoUnitRate(string photoSizeCode, ShopPricingProfile pricing) =>
        photoSizeCode switch
        {
            "35x45" => pricing.Photo35x45Minor,
            "25x35" => pricing.Photo25x35Minor,
            "2x2" => pricing.Photo2x2Minor,
            "4x6" => pricing.Photo4x6Minor,
            _ => throw new ArgumentException("Choose a supported photo size.", nameof(photoSizeCode))
        };

    public static int MaximumPhotosPerA4Sheet(string photoSizeCode) =>
        photoSizeCode switch
        {
            "35x45" => 25,
            "25x35" => 49,
            "2x2" => 15,
            "4x6" => 1,
            _ => throw new ArgumentException("Choose a supported photo size.", nameof(photoSizeCode))
        };

    public static int PhotoPageCount(int photoCount, string photoSizeCode)
    {
        if (photoCount is < 1 or > UploadLimits.MaxPhotoCount)
            throw new ArgumentOutOfRangeException(nameof(photoCount), $"Photo count must be between 1 and {UploadLimits.MaxPhotoCount}.");

        var photosPerPage = MaximumPhotosPerA4Sheet(photoSizeCode);
        return checked((photoCount + photosPerPage - 1) / photosPerPage);
    }

    public static int PhotoPageCount(int photoCount, string photoSizeCode, int photosPerPage)
    {
        if (photoCount is < 1 or > UploadLimits.MaxPhotoCount)
            throw new ArgumentOutOfRangeException(nameof(photoCount), $"Photo count must be between 1 and {UploadLimits.MaxPhotoCount}.");

        var maximumPhotos = MaximumPhotosPerA4Sheet(photoSizeCode);
        if (photosPerPage is < 1 || photosPerPage > maximumPhotos)
            throw new ArgumentOutOfRangeException(nameof(photosPerPage), $"Photos per A4 page must be between 1 and {maximumPhotos} for the selected size.");

        return checked((photoCount + photosPerPage - 1) / photosPerPage);
    }

    public static long[] CalculatePhotoPageRefundAllocations(
        int photoCount, string photoSizeCode, int photosPerPage, long photoUnitPriceMinor)
    {
        if (photoUnitPriceMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(photoUnitPriceMinor), "The per-photo rate must be positive.");

        var pageCount = PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
        var allocations = new long[pageCount];
        var remainingPhotos = photoCount;
        long allocated = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var pagePhotos = Math.Min(photosPerPage, remainingPhotos);
            allocations[page] = checked((long)pagePhotos * photoUnitPriceMinor);
            allocated = checked(allocated + allocations[page]);
            remainingPhotos -= pagePhotos;
        }

        var expectedTotal = checked((long)photoCount * photoUnitPriceMinor);
        if (remainingPhotos != 0 || allocated != expectedTotal)
            throw new InvalidOperationException("The per-page photo refund allocation does not equal the quoted photo total.");
        return allocations;
    }

    public static long[] CalculatePageRefundAllocations(int pageCount, int copies, string colourMode, bool isDuplex, ShopPricingProfile pricing)
    {
        var total = Calculate(pageCount, copies, colourMode, isDuplex, pricing);
        var weights = Enumerable.Repeat(1L, pageCount).ToArray();
        if (isDuplex && pageCount % 2 == 1) weights[^1] = 2;

        var totalWeight = weights.Aggregate(0L, (sum, weight) => checked(sum + weight));
        var allocations = new long[pageCount];
        var remainders = new List<(int Index, decimal Remainder)>();
        long allocated = 0;
        for (var index = 0; index < weights.Length; index++)
        {
            var exact = (decimal)total * weights[index] / totalWeight;
            var whole = decimal.ToInt64(decimal.Floor(exact));
            allocations[index] = whole;
            allocated = checked(allocated + whole);
            remainders.Add((index, exact - whole));
        }

        var remainingMinorUnits = checked(total - allocated);
        foreach (var remainder in remainders.OrderByDescending(item => item.Remainder).ThenBy(item => item.Index).Take(checked((int)remainingMinorUnits)))
            allocations[remainder.Index] = checked(allocations[remainder.Index] + 1);

        if (allocations.Aggregate(0L, (sum, value) => checked(sum + value)) != total)
            throw new InvalidOperationException("The page refund allocation does not equal the original quote.");
        return allocations;
    }
}

public sealed class DuplexPricingNotConfiguredException()
    : InvalidOperationException("Duplex printing is not currently priced by this shop.");
