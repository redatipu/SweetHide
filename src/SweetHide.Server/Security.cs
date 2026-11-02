using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace SweetHide.Server;

/// <summary>Headers that lock the browser UI down: no inline script or style, no framing, no referrers.</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Everything the page loads comes from this server. blob: is for previews of images the user picks and for the
    /// files and bit plane the server returns, which the page turns into object URLs.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; " +
        "manifest-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["X-Robots-Tag"] = "noindex, nofollow";
        if (context.Request.Path.StartsWithSegments(ServerApp.ApiPrefix, StringComparison.Ordinal))
        {
            // Responses carry secrets (revealed messages and files) or are derived from them.
            headers.CacheControl = "no-store";
        }

        return next(context);
    }
}

/// <summary>
/// Optional shared secret (<see cref="ServerOptions.AccessToken"/>). Accepts <c>Authorization: Bearer &lt;token&gt;</c>
/// for scripts and HTTP Basic auth with the token as the password (any user name), so browsers show their own login
/// prompt and then send it automatically. <c>/healthz</c> stays open for container health checks.
/// </summary>
internal sealed class AccessTokenMiddleware(RequestDelegate next, IOptions<ServerOptions> options, IProblemDetailsService problems)
{
    private readonly byte[]? _tokenHash = options.Value.RequiresAccessToken ? Hash(options.Value.AccessToken!) : null;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_tokenHash is null || context.Request.Path.StartsWithSegments(ServerApp.HealthPath, StringComparison.Ordinal) || IsAuthorized(context.Request))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"SweetHide\", charset=\"UTF-8\"";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ProblemExceptionHandler.Create(
                StatusCodes.Status401Unauthorized,
                ProblemCodes.Unauthorized,
                "An access token is required.",
                "Use HTTP Basic auth with the token as the password, or an 'Authorization: Bearer <token>' header."),
        });
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) || string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        string? presented = null;
        if (string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            presented = header.Parameter;
        }
        else if (string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            presented = BasicPassword(header.Parameter);
        }

        // Comparing fixed-size hashes in constant time leaks neither content nor length.
        return presented is not null && CryptographicOperations.FixedTimeEquals(Hash(presented), _tokenHash);
    }

    private static string? BasicPassword(string encoded)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return colon >= 0 ? decoded[(colon + 1)..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

/// <summary>
/// Rejects API calls that a browser marks as coming from another site. The API changes nothing on the server, but this
/// stops other web pages from using a visitor's cached login (or LAN position) to make the server do work.
/// Clients that don't send Sec-Fetch-Site, such as curl, are unaffected.
/// </summary>
internal sealed class CrossSiteGuardMiddleware(RequestDelegate next, IProblemDetailsService problems)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (context.Request.Path.StartsWithSegments(ServerApp.ApiPrefix, StringComparison.Ordinal)
            && (string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase) || string.Equals(site, "same-site", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = ProblemExceptionHandler.Create(
                    StatusCodes.Status403Forbidden, ProblemCodes.CrossSite, "Requests from other web sites are not allowed."),
            });
            return;
        }

        await next(context);
    }
}
