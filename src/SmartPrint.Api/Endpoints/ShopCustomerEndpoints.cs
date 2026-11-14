using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopCustomerEndpoints
{
    public static RouteGroupBuilder MapShopCustomerEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/customers", async (
            ClaimsPrincipal principal,
            ShopCustomerQueryService customers,
            string? search,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
            var result = await customers.ListAsync(
                tenantId, search, pageNumber, boundedPageSize, ct);
            return Results.Ok(result);
        });

        shop.MapGet("/customers/{customerId:guid}/jobs", async (
            Guid customerId,
            ClaimsPrincipal principal,
            ShopCustomerQueryService customers,
            int? take,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var boundedTake = Math.Clamp(take ?? 50, 1, 100);
            var result = await customers.GetCustomerJobsAsync(
                tenantId, customerId, boundedTake, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return shop;
    }
}