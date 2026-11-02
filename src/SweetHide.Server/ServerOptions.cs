using System.Net;

using Microsoft.Extensions.Options;

using SweetHide.Core.Imaging;

namespace SweetHide.Server;

/// <summary>
/// Settings from the "SweetHide" configuration section, e.g. appsettings.json or environment variables such as
/// <c>SWEETHIDE__ACCESSTOKEN</c>. See docs/SERVER.md.
/// </summary>
internal sealed class ServerOptions
{
    public const string SectionName = "SweetHide";

    public const long MinRequestBytes = 1024;
    public const long MaxRequestBytesCeiling = 1024L * 1024 * 1024;

    /// <summary>
    /// When set, every request except <c>/healthz</c> needs <c>Authorization: Bearer &lt;token&gt;</c> or HTTP Basic
    /// auth with this token as the password (any user name), which is what browsers prompt for.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Largest request body accepted, uploads included.</summary>
    public long MaxRequestBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Largest image (width × height) the server will decode.</summary>
    public long MaxImagePixels { get; set; } = SkiaImageCodec.DefaultMaxPixels;

    /// <summary>Image jobs (Argon2, decoding, embedding) that may run at the same time.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Further API requests that may wait (or still be uploading); beyond that the server answers 503.</summary>
    public int MaxQueuedJobs { get; set; } = 16;

    /// <summary>Requests per minute from one IP address (everything except <c>/healthz</c>).</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>A request that takes longer than this (upload, queueing and work) is cancelled where possible.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Reverse proxies (IP addresses or CIDR ranges such as 172.16.0.0/12) whose X-Forwarded-For header is trusted
    /// for rate limiting. Empty means the header is ignored.
    /// </summary>
    public IList<string> TrustedProxies { get; } = [];

    public bool RequiresAccessToken => !string.IsNullOrEmpty(AccessToken);

    public static bool TryParseProxy(string value, out IPAddress? address, out IPNetwork? network)
    {
        address = null;
        network = null;
        var trimmed = value.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            if (IPNetwork.TryParse(trimmed, out var parsed))
            {
                network = parsed;
                return true;
            }

            return false;
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            address = ip;
            return true;
        }

        return false;
    }
}

internal sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        var failures = new List<string>();
        if (options.MaxRequestBytes is < ServerOptions.MinRequestBytes or > ServerOptions.MaxRequestBytesCeiling)
        {
            failures.Add($"{ServerOptions.SectionName}:MaxRequestBytes must be between {ServerOptions.MinRequestBytes} and {ServerOptions.MaxRequestBytesCeiling}.");
        }

        if (options.MaxImagePixels <= 0)
        {
            failures.Add($"{ServerOptions.SectionName}:MaxImagePixels must be positive.");
        }

        if (options.MaxConcurrentJobs is < 1 or > 1000)
        {
            failures.Add($"{ServerOptions.SectionName}:MaxConcurrentJobs must be between 1 and 1000.");
        }

        if (options.MaxQueuedJobs is < 0 or > 10_000)
        {
            failures.Add($"{ServerOptions.SectionName}:MaxQueuedJobs must be between 0 and 10000.");
        }

        if (options.RateLimitPerMinute is < 1 or > 1_000_000)
        {
            failures.Add($"{ServerOptions.SectionName}:RateLimitPerMinute must be between 1 and 1000000.");
        }

        if (options.RequestTimeoutSeconds is < 1 or > 86_400)
        {
            failures.Add($"{ServerOptions.SectionName}:RequestTimeoutSeconds must be between 1 and 86400.");
        }

        foreach (var proxy in options.TrustedProxies)
        {
            if (!ServerOptions.TryParseProxy(proxy, out _, out _))
            {
                failures.Add($"{ServerOptions.SectionName}:TrustedProxies contains '{proxy}', which is not an IP address or CIDR range.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
