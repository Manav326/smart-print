using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class AccountRegistrationEndpoints
{
    public static RouteGroupBuilder MapAccountRegistrationEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/register-shop", async (
            RegisterShopRequest request,
            AccountRegistrationService registration,
            CancellationToken ct) =>
        {
            var result = await registration.RegisterShopAsync(request, ct);
            if (result.Failure == AccountRegistrationFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["shop"] = ["A valid business name, owner name, email address and password are required."]
                });
            }

            if (result.Failure == AccountRegistrationFailure.EmailAlreadyRegistered)
            {
                return Results.Conflict(new
                {
                    code = "email_already_registered",
                    message = "An account already exists for this email address."
                });
            }

            if (result.Failure == AccountRegistrationFailure.IdentityValidationFailed)
                return Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>());

            if (result.Failure == AccountRegistrationFailure.PermissionAssignmentFailed)
                return Results.Problem("Could not create the shop owner permissions.", statusCode: 500);

            return Results.Created("/api/v1/shop/profile", result.Response!);
        }).AllowAnonymous();

        auth.MapPost("/register-customer", async (
            RegisterCustomerRequest request,
            AccountRegistrationService registration,
            CancellationToken ct) =>
        {
            var result = await registration.RegisterCustomerAsync(request, ct);
            if (result.Failure == AccountRegistrationFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customer"] = ["A display name, valid email address and password are required."]
                });
            }

            if (result.Failure == AccountRegistrationFailure.EmailAlreadyRegistered)
            {
                return Results.Conflict(new
                {
                    code = "email_already_registered",
                    message = "An account already exists for this email address."
                });
            }

            if (result.Failure == AccountRegistrationFailure.IdentityValidationFailed)
                return Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>());

            if (result.Failure == AccountRegistrationFailure.PermissionAssignmentFailed)
                return Results.Problem("Could not create the customer account permissions.", statusCode: 500);

            return Results.Created("/api/v1/customer/me", result.Tokens!);
        }).AllowAnonymous();
        return auth;
    }
}
