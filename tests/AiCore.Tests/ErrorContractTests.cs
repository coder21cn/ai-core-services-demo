using System.Net;
using System.Reflection;
using AiCore.Api.Errors;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

/// <summary>
/// The Problem Details <c>type</c> is the RFC 9457 identifier of an error, so it is part of the public
/// contract. Each one must resolve to the documentation of its code.
/// </summary>
public sealed class ErrorContractTests
{
    private const string TypeBase = "https://github.com/coder21cn/ai-core-services-demo/blob/main/README.md#error-";

    public static TheoryData<string> AllErrorCodes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllErrorCodes))]
    public void EveryErrorCode_HasTheReadmeAnchorItsTypeLinksTo(string code)
    {
        Assert.Contains($"<a id=\"error-{code}\"></a>`{code}`", File.ReadAllText(FindReadme()));
    }

    [Fact]
    public async Task ProblemType_IsTheReadmeAnchorForItsCode()
    {
        using var api = new TestApi();
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, "no-such-alias", "hi");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TypeBase + "unknown_model", (await TestApi.ReadJson(response)).GetProperty("type").GetString());
    }

    private static string FindReadme()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var readme = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(readme) && File.Exists(Path.Combine(dir.FullName, "AiCore.slnx")))
            {
                return readme;
            }
        }

        throw new FileNotFoundException("Repository README.md not found above the test output directory.");
    }
}
