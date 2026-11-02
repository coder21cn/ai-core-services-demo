using Microsoft.AspNetCore.Mvc;
using AiCore.Api.Observability;

namespace AiCore.Api.Errors;

/// <summary>Stable machine-readable error codes. Clients should branch on <c>code</c>, not on <c>title</c>.</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string UnknownModel = "unknown_model";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string RateQuotaExceeded = "rate_quota_exceeded";
    public const string ConcurrencyQuotaExceeded = "concurrency_quota_exceeded";
    public const string CapacityExhausted = "capacity_exhausted";
    public const string ProviderTimeout = "provider_timeout";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProviderError = "provider_error";
    public const string InternalError = "internal_error";
    public const string RequestTooLarge = "request_too_large";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
}

/// <summary>
/// A safe, caller-facing error. Every field is authored by this service; exception messages,
/// prompt text and credentials are never copied into it.
/// </summary>
public sealed record ApiError(
    int Status,
    string Code,
    string Title,
    string Detail,
    IDictionary<string, string[]>? Errors = null,
    TimeSpan? RetryAfter = null);

/// <summary>Builds RFC 9457 Problem Details bodies with <c>code</c> and <c>correlationId</c> extensions.</summary>
public static class ApiProblem
{
    public const string ContentType = "application/problem+json";

    public static ApiError Validation(IDictionary<string, string[]> errors) => new(
        StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
        "The request is invalid.", "One or more fields failed validation.", errors);

    public static ApiError MalformedBody(string detail) => new(
        StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "The request is invalid.", detail);

    public static ApiError RequestTooLarge() => new(
        StatusCodes.Status413PayloadTooLarge, ErrorCodes.RequestTooLarge,
        "Request too large.", "The request body exceeds the configured size limit.");

    /// <summary>Body for framework-generated empty 4xx/5xx responses (404, 405, ...).</summary>
    public static ApiError ForStatus(int status) => status switch
    {
        StatusCodes.Status404NotFound => new(status, ErrorCodes.NotFound, "Not found.", "No resource matches the request path."),
        StatusCodes.Status405MethodNotAllowed => new(status, ErrorCodes.MethodNotAllowed, "Method not allowed.", "The HTTP method is not supported for this path."),
        >= 500 => Internal() with { Status = status },
        _ => new(status, ErrorCodes.InvalidRequest, "The request is invalid.", "The request could not be processed."),
    };

    public static ApiError UnsupportedMediaType() => new(
        StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaType,
        "Unsupported media type.", "Requests must use Content-Type: application/json.");

    public static ApiError UnknownModel() => new(
        StatusCodes.Status400BadRequest, ErrorCodes.UnknownModel,
        "Unknown model alias.", "The requested model alias is not configured.");

    public static ApiError Unauthenticated(string detail) => new(
        StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication required.", detail);

    public static ApiError Forbidden() => new(
        StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
        "Access denied.", "The authenticated tenant is not permitted to perform this operation.");

    /// <summary>The tenant's request-rate quota is empty. 429: caused by this tenant's own usage.</summary>
    public static ApiError RateQuotaExceeded(TimeSpan retryAfter) => new(
        StatusCodes.Status429TooManyRequests, ErrorCodes.RateQuotaExceeded,
        "Tenant request quota exceeded.",
        "The tenant's request-rate quota is used up. Retry after the number of seconds in Retry-After.",
        RetryAfter: retryAfter);

    /// <summary>The tenant already has its maximum requests in progress. 429: caused by this tenant's own usage.</summary>
    public static ApiError ConcurrencyQuotaExceeded(TimeSpan retryAfter) => new(
        StatusCodes.Status429TooManyRequests, ErrorCodes.ConcurrencyQuotaExceeded,
        "Tenant concurrency quota exceeded.",
        "The tenant already has the maximum number of requests in progress. Retry when one of them completes.",
        RetryAfter: retryAfter);

    /// <summary>Global provider capacity is full. 503 (not 429): this is platform overload, not a tenant quota.</summary>
    public static ApiError CapacityExhausted(bool queueWaitTimedOut = false) => new(
        StatusCodes.Status503ServiceUnavailable, ErrorCodes.CapacityExhausted,
        "Service at capacity.",
        queueWaitTimedOut
            ? "No model-call capacity became free within the maximum queue wait. Retry later."
            : "Too many model calls are in progress. Retry later.",
        RetryAfter: TimeSpan.FromSeconds(1));

    /// <summary>
    /// The provider phase exceeded its deadline. No Retry-After: the provider may still have completed (and
    /// billed) the request, so whether to retry is the caller's decision.
    /// </summary>
    public static ApiError ProviderTimeout() => new(
        StatusCodes.Status504GatewayTimeout, ErrorCodes.ProviderTimeout,
        "Model provider timeout.",
        "The model provider did not respond within the configured timeout. It may still have processed the request, " +
        "so a retry can produce a second, separately billed completion.");

    public static ApiError ProviderUnavailable() => new(
        StatusCodes.Status503ServiceUnavailable, ErrorCodes.ProviderUnavailable,
        "Model provider unavailable.", "The model provider reported a transient failure. Retry later.",
        RetryAfter: TimeSpan.FromSeconds(1));

    /// <summary>
    /// Calls to this model are paused locally because earlier calls to it have not finished (abandoned-call limits).
    /// Same code as <see cref="ProviderUnavailable"/> (the client reaction is the same), but the detail says that
    /// nothing was sent, so client and operator can tell it apart from a failure the provider reported.
    /// </summary>
    public static ApiError ProviderPaused(TimeSpan retryAfter) => new(
        StatusCodes.Status503ServiceUnavailable, ErrorCodes.ProviderUnavailable,
        "Model provider unavailable.",
        "Calls to this model are paused because earlier calls to it have not finished; no request was sent to the provider. Retry later.",
        RetryAfter: retryAfter);

    public static ApiError ProviderError() => new(
        StatusCodes.Status502BadGateway, ErrorCodes.ProviderError,
        "Model provider error.", "The model provider rejected or failed the request. Retrying will not help.");

    public static ApiError Internal() => new(
        StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
        "Internal error.", "An unexpected error occurred. Quote the correlation ID when reporting it.");

    public static ProblemDetails ToProblemDetails(HttpContext context, ApiError error)
    {
        ProblemDetails problem = error.Errors is null
            ? new ProblemDetails()
            : new HttpValidationProblemDetails(error.Errors);

        problem.Status = error.Status;
        problem.Title = error.Title;
        problem.Detail = error.Detail;
        problem.Type = $"https://github.com/coder21cn/ai-core-services-demo/blob/main/README.md#error-{error.Code}";
        problem.Extensions["code"] = error.Code;
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        return problem;
    }

    public static IResult ToResult(HttpContext context, ApiError error)
    {
        ApplyHeaders(context, error);
        // Typed as object so the runtime type (e.g. HttpValidationProblemDetails with "errors") is serialized.
        object body = ToProblemDetails(context, error);
        return Results.Json(body, (System.Text.Json.JsonSerializerOptions?)null, ContentType, error.Status);
    }

    public static Task Write(HttpContext context, ApiError error)
    {
        context.Response.StatusCode = error.Status;
        ApplyHeaders(context, error);
        var body = ToProblemDetails(context, error);
        return context.Response.WriteAsJsonAsync(body, body.GetType(), options: null, contentType: ContentType);
    }

    private static void ApplyHeaders(HttpContext context, ApiError error)
    {
        RequestObservation.For(context).ErrorCode = error.Code;
        if (error.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
    }
}
