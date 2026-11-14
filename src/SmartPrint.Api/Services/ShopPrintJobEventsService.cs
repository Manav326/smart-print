using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public sealed class ShopPrintJobEventsService(SmartPrintDbContext db)
{
    public async Task<object?> GetEventsAsync(Guid tenantId, Guid jobId, CancellationToken ct)
    {
        if (!await db.PrintJobs.AsNoTracking().AnyAsync(
                job => job.Id == jobId && job.TenantId == tenantId, ct))
        {
            return null;
        }

        return await db.PrintJobEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
            .OrderBy(item => item.CreatedUtc)
            .Select(item => new
            {
                item.Id,
                item.PreviousStatus,
                item.NewStatus,
                item.ActorType,
                item.ReasonCode,
                item.CreatedUtc
            })
            .ToListAsync(ct);
    }
}
