using System.Net;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

public sealed class PrivacyRegressionTests
{
    private const string Marker = "PRIVATE-PROMPT-credential-947123";

    [Theory]
    [InlineData("permanent")]
    [InlineData("transient")]
    [InlineData("retry")]
    public async Task T08_ProviderExceptionMessages_NeverReachLogsOrErrors(string failure)
    {
        using var api = new TestApi(new() { ["AiCore:Resilience:RetryBackoffMs"] = "0" },
            new ThrowingProvider(() => failure == "permanent"
                ? new ProviderPermanentException(Marker)
                : new ProviderTransientException(Marker, requestNotProcessed: failure == "retry")));
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
        Assert.Equal(failure == "permanent" ? HttpStatusCode.BadGateway : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(Marker, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(Marker));
    }

    [Fact]
    public async Task T08_UnknownJsonProperty_IsNotEchoedInError()
    {
        using var api = new TestApi();
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var response = await TestApi.PostRaw(client,
            $$"""{"model":"chat-default","prompt":"hi","{{Marker}}":true}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(Marker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task T08_UnhandledServiceException_IsNotLoggedInFull()
    {
        using var api = new TestApi(extraProvider: new BrokenNameProvider());
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(Marker, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(Marker));
        var completion = Assert.Single(api.Logs.Entries, e => e.Fields.ContainsKey("Result"));
        Assert.Equal("/v1/generate", completion.Fields["Endpoint"]);
    }

    // Fail during service resolution, outside ProviderInvoker's exception mapping.
    private sealed class BrokenNameProvider : IModelProvider
    {
        public string Name => throw new InvalidOperationException(Marker);
        public Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
