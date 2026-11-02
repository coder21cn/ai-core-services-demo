using System.Security.Cryptography;
using System.Text;
using AiCore.Api.Configuration;

namespace AiCore.Api.Authentication;

/// <summary>
/// Maps a presented API key to a tenant. The store indexes SHA-256 digests and looks up by digest, so
/// secrets are never compared with an early-exit string comparison that could leak key prefixes.
/// (Plaintext keys still exist in the configuration system, as for any config-sourced secret.)
/// DEVELOPMENT-ONLY: production would validate OIDC/OAuth2 tokens or workload identity instead.
/// </summary>
public sealed class TenantKeyStore
{
    private readonly Dictionary<string, string> _tenantByKeyDigest = new(StringComparer.Ordinal);

    public TenantKeyStore(IEnumerable<TenantApiKeyOptions> tenants)
    {
        foreach (var tenant in tenants)
        {
            // These checks guard lookup correctness; broader config validation is an M2 item.
            if (string.IsNullOrWhiteSpace(tenant.TenantId) || string.IsNullOrWhiteSpace(tenant.ApiKey))
            {
                throw new InvalidOperationException(
                    "AiCore:Tenants entries require both TenantId and ApiKey.");
            }

            if (!_tenantByKeyDigest.TryAdd(Digest(tenant.ApiKey), tenant.TenantId))
            {
                // Do not include the key or the tenant that already owns it in the message.
                throw new InvalidOperationException(
                    $"AiCore:Tenants contains a duplicate ApiKey (tenant '{tenant.TenantId}').");
            }
        }
    }

    public int Count => _tenantByKeyDigest.Count;

    public bool TryResolveTenant(string apiKey, out string tenantId) =>
        _tenantByKeyDigest.TryGetValue(Digest(apiKey), out tenantId!);

    private static string Digest(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
