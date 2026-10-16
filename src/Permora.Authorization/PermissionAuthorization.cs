using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Permora.Authorization;

public sealed record PermissionRequirement(string ClientId, string PermissionKey) : IAuthorizationRequirement;

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;
        foreach (var claim in context.User.FindAll("resource_access"))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(requirement.ClientId, out var client)
                    && client.ValueKind == JsonValueKind.Object && client.TryGetProperty("roles", out var roles)
                    && roles.ValueKind == JsonValueKind.Array
                    && roles.EnumerateArray().Any(role => role.ValueKind == JsonValueKind.String
                        && role.GetString() == "perm." + requirement.PermissionKey))
                    context.Succeed(requirement);
            }
            catch (JsonException) { /* Malformed authorization data fails closed. */ }
        }
        return Task.CompletedTask;
    }
}

public static class ServiceCollectionExtensions
{
    // The host must validate JWT signature, issuer, audience and lifetime with its authentication handler.
    public static IServiceCollection AddPermoraPermission(this IServiceCollection services,
        string policyName, string clientId, string permissionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionHandler>());
        services.AddAuthorization(options => options.AddPolicy(policyName, policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(clientId, permissionKey))));
        return services;
    }
}
