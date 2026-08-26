namespace SoftPhone.Core.Config;

public enum DomainValidationResult
{
    /// <summary>Reachable and the Soft Phone Extension feature is present (may still need login).</summary>
    Valid,
    /// <summary>Reachable, but the Soft Phone Extension feature is not enabled (404).</summary>
    FeatureNotEnabled,
    /// <summary>Not a usable domain (DNS/network failure or a non-telephony server).</summary>
    Unreachable,
}

/// <summary>
/// Validates a tenant domain during first-run setup <em>without</em> a session: it probes
/// <c>/softphone/extension-config</c> and interprets the outcome. An unauthenticated
/// redirect (302/401/403) means "reachable + feature present, just not signed in yet" →
/// Valid; a 404 means the feature is off; a network error means unreachable.
/// </summary>
public static class DomainValidator
{
    public static Task<DomainValidationResult> ValidateAsync(string domain, CancellationToken ct = default)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var client = new ConfigClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) });
        return ValidateAsync(domain, client, ct);
    }

    public static async Task<DomainValidationResult> ValidateAsync(string domain, ConfigClient client, CancellationToken ct = default)
    {
        if (!DomainHelper.IsValidDomain(domain)) return DomainValidationResult.Unreachable;
        try
        {
            await client.FetchExtensionConfigAsync(domain, ct);
            return DomainValidationResult.Valid; // 200 with a valid config
        }
        catch (ConfigException e)
        {
            return e.Kind switch
            {
                ConfigErrorKind.Unauthenticated => DomainValidationResult.Valid,
                ConfigErrorKind.NotEnabled => DomainValidationResult.FeatureNotEnabled,
                _ => DomainValidationResult.Unreachable,
            };
        }
    }
}
