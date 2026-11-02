namespace AiCore.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /v1/generate</c>. Note there is deliberately no tenant field:
/// the tenant is derived from the authenticated credential (FR-03). Unknown JSON members are rejected.
/// </summary>
public sealed record GenerateRequest
{
    /// <summary>Logical model alias, e.g. "chat-default". Not a vendor model name.</summary>
    public string? Model { get; init; }

    public string? Prompt { get; init; }

    /// <summary>Optional output bound. Defaults to <c>AiCore:Limits:DefaultOutputTokens</c>.</summary>
    public int? MaxOutputTokens { get; init; }
}

public sealed record GenerateResponse(
    string CorrelationId,
    string Model,
    string Provider,
    string ProviderModel,
    string Output,
    long LatencyMs,
    UsageDto Usage);

/// <summary>Token counts are estimates produced by the mock provider; <c>Simulated</c> makes that explicit.</summary>
public sealed record UsageDto(int InputTokens, int OutputTokens, int TotalTokens, bool Simulated);
