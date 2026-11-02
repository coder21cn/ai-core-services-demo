using System.Security.Claims;
using System.Text.Encodings.Web;
using AiCore.Api.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiCore.Api.Authentication;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string TenantClaim = "tenant_id";
}

/// <summary>
/// Development API-key authentication. The tenant identity is taken solely from the key lookup;
/// any tenant identifiers in request headers or body are ignored or rejected (FR-03).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TenantKeyStore keyStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string FailureItemKey = "AiCore.AuthFailure";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values) ||
            values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            Context.Items[FailureItemKey] = values.Count > 1 ? "invalid" : "missing";
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!keyStore.TryResolveTenant(values[0]!, out var tenantId))
        {
            Context.Items[FailureItemKey] = "invalid";
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ApiKeyDefaults.TenantClaim, tenantId)], ApiKeyDefaults.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var detail = Context.Items[FailureItemKey] as string == "invalid"
            ? "The supplied API key is not valid."
            : $"An API key must be supplied in the {ApiKeyDefaults.HeaderName} header.";

        Response.Headers.WWWAuthenticate = ApiKeyDefaults.Scheme;
        await ApiProblem.Write(Context, ApiProblem.Unauthenticated(detail));
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        // Not reachable in M1 (no per-tenant authorization policies yet); kept so 403 has the same contract.
        await ApiProblem.Write(Context, ApiProblem.Forbidden());
    }
}
