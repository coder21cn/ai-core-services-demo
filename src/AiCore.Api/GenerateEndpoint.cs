using System.Text.Json;
using System.Text.Json.Serialization;
using AiCore.Api.Authentication;
using AiCore.Api.Configuration;
using AiCore.Api.Contracts;
using AiCore.Api.Errors;
using AiCore.Api.Execution;
using AiCore.Api.Observability;

namespace AiCore.Api;

/// <summary>
/// HTTP contract for <c>POST /v1/generate</c>: content negotiation, strict JSON parsing, input bounds,
/// and mapping of the generation outcome to the response/error contract.
/// </summary>
public static class GenerateEndpoint
{
    // Strict parsing: unknown members (including any attempt to pass "tenantId") are rejected with 400
    // rather than silently ignored, which also catches client typos such as "promt". The Web defaults are
    // tightened as well, so the accepted contract can't widen by accident: member names must match exactly
    // ("model", not "MODEL"), numbers must be JSON numbers (not "5"), and a member may appear only once.
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
    };

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        GenerationService generation,
        RequestLimitsOptions limits,
        AiCoreTelemetry telemetry,
        IReadOnlyDictionary<string, ModelRouteOptions> routes)
    {
        // Authorization has already run; the tenant is taken ONLY from the authenticated principal.
        var tenantId = context.User.FindFirst(ApiKeyDefaults.TenantClaim)?.Value;
        if (string.IsNullOrEmpty(tenantId))
        {
            return ApiProblem.ToResult(context, ApiProblem.Unauthenticated("No tenant identity is associated with the credential."));
        }

        if (!context.Request.HasJsonContentType())
        {
            return ApiProblem.ToResult(context, ApiProblem.UnsupportedMediaType());
        }

        GenerateRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<GenerateRequest>(
                context.Request.Body, RequestJson, context.RequestAborted);
        }
        catch (JsonException)
        {
            // Even the JSON path can contain a caller-supplied sensitive property name.
            return ApiProblem.ToResult(context, ApiProblem.MalformedBody("The request body is not valid JSON for this endpoint."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiProblem.ToResult(context, ApiProblem.RequestTooLarge());
        }

        if (request is null)
        {
            return ApiProblem.ToResult(context, ApiProblem.MalformedBody("A JSON object body is required."));
        }

        var errors = Validate(request, limits);
        if (errors.Count > 0)
        {
            return ApiProblem.ToResult(context, ApiProblem.Validation(errors));
        }

        var observation = RequestObservation.For(context);
        if (routes.TryGetValue(request.Model!, out var route))
        {
            observation.Model = telemetry.CanonicalAlias(request.Model!);
            observation.Provider = route.Provider;
        }

        var command = new GenerationCommand(
            context.TraceIdentifier,
            tenantId,
            request.Model!,
            request.Prompt!,
            // The default is capped so lowering MaxOutputTokens always bounds the response, even when
            // DefaultOutputTokens was left above it.
            request.MaxOutputTokens ?? Math.Min(limits.DefaultOutputTokens, limits.MaxOutputTokens));

        var outcome = await generation.GenerateAsync(command, context.RequestAborted);
        if (outcome.Error is { } error)
        {
            return ApiProblem.ToResult(context, error);
        }

        var success = outcome.Success!;
        var result = success.Result;
        observation.InputTokens = result.InputTokens;
        observation.OutputTokens = result.OutputTokens;
        return Results.Ok(new GenerateResponse(
            context.TraceIdentifier,
            success.ModelAlias,
            success.Provider,
            success.ProviderModel,
            result.Output,
            (long)success.ProviderLatency.TotalMilliseconds,
            new UsageDto(result.InputTokens, result.OutputTokens, result.InputTokens + result.OutputTokens, Simulated: true)));
    }

    private static Dictionary<string, string[]> Validate(GenerateRequest request, RequestLimitsOptions limits)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            errors["model"] = ["A logical model alias is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            errors["prompt"] = ["A non-empty prompt is required."];
        }
        else if (request.Prompt.Length > limits.MaxPromptChars)
        {
            errors["prompt"] = [$"The prompt must not exceed {limits.MaxPromptChars} characters."];
        }

        if (request.MaxOutputTokens is { } max && (max < 1 || max > limits.MaxOutputTokens))
        {
            errors["maxOutputTokens"] = [$"maxOutputTokens must be between 1 and {limits.MaxOutputTokens}."];
        }

        return errors;
    }
}
