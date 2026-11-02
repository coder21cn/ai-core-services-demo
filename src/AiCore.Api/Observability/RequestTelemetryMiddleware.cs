using System.Diagnostics;
using AiCore.Api.Authentication;
using AiCore.Api.Errors;

namespace AiCore.Api.Observability;

public sealed class RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AiCoreTelemetry telemetry)
    {
        var started = Stopwatch.GetTimestamp();
        var observation = RequestObservation.For(context);
        // Exception handling clears endpoint routing metadata. Capture it before running that middleware.
        var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText switch
        {
            "/v1/generate" => "/v1/generate",
            "/health/live" => "/health/live",
            _ => "unmatched",
        };
        using var activity = telemetry.StartActivity("aicore.request");
        activity?.SetTag("correlation.id", context.TraceIdentifier);
        var cancelled = false;
        var escapedFailure = false;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            cancelled = true;
            throw;
        }
        catch (Exception)
        {
            // An error after response headers started cannot be rewritten by the exception handler.
            // Still classify it as failure rather than reporting a successful default/partial response.
            escapedFailure = true;
            throw;
        }
        finally
        {
            // ExceptionHandlerMiddleware handles client disconnects itself; include that path too.
            cancelled |= context.RequestAborted.IsCancellationRequested;
            var status = cancelled ? 499 : escapedFailure && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
            var result = cancelled ? "cancelled" : escapedFailure ? ErrorCodes.InternalError : observation.ErrorCode ??
                (status < 400 ? "success" : ApiProblem.ForStatus(status).Code);
            var tenant = telemetry.TenantReference(context.User.FindFirst(ApiKeyDefaults.TenantClaim)?.Value);
            var elapsed = Stopwatch.GetElapsedTime(started);
            activity?.SetTag("tenant.ref", tenant);
            activity?.SetTag("model.alias", observation.Model);
            activity?.SetTag("provider.name", observation.Provider);
            activity?.SetTag("result", result);
            activity?.SetTag("http.response.status_code", status);
            activity?.SetTag("usage.input_tokens", observation.InputTokens);
            activity?.SetTag("usage.output_tokens", observation.OutputTokens);
            activity?.SetTag("usage.simulated", true);
            if (!cancelled && (escapedFailure || status >= 500)) activity?.SetStatus(ActivityStatusCode.Error);
            telemetry.RecordRequest(observation, endpoint, result, status, elapsed);
            telemetry.Observe(() => logger.LogInformation(
                "Request completed: {CorrelationId} trace {TraceId} tenant {TenantRef} endpoint {Endpoint} model {ModelAlias} " +
                "provider {Provider} result {Result} status {StatusCode} duration {DurationMs} ms " +
                "input {InputTokens} output {OutputTokens} simulated {UsageSimulated}",
                context.TraceIdentifier, Activity.Current?.TraceId.ToString(), tenant, endpoint, observation.Model,
                observation.Provider, result, status, elapsed.TotalMilliseconds,
                observation.InputTokens, observation.OutputTokens, true));
        }
    }
}
