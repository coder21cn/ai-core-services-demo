namespace AiCore.Api.Execution;

/// <summary>
/// A route is one provider model behind one provider adapter (e.g. <c>mock/mock-echo-v1</c>). Abandoned calls are
/// counted per route, because one model can hang while other models of the same provider are healthy.
/// </summary>
public static class ProviderRoutes
{
    public static string Key(string providerName, string providerModel) => $"{providerName}/{providerModel}";
}
