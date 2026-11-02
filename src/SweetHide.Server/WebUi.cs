using System.Collections.Frozen;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace SweetHide.Server;

/// <summary>
/// The browser UI: a few static files compiled into the assembly (web/*) and kept in memory. index.html gets the
/// language picked from Accept-Language plus a few server limits the page uses to warn before uploading.
/// </summary>
internal sealed class WebUi
{
    public const string IndexFile = "index.html";
    private const string ResourcePrefix = "web/";

    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".webmanifest"] = "application/manifest+json; charset=utf-8",
        [".png"] = "image/png",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, StaticAsset> _assets;
    private readonly FrozenDictionary<string, StaticAsset> _index;

    public WebUi(IOptions<ServerOptions> options)
    {
        var assembly = typeof(WebUi).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), name => Read(assembly, name), StringComparer.Ordinal);

        if (!files.Remove(IndexFile, out var indexTemplate))
        {
            throw new InvalidOperationException($"The embedded UI is missing {IndexFile}.");
        }

        _assets = files.ToFrozenDictionary(pair => pair.Key, pair => new StaticAsset(pair.Value, ContentTypeOf(pair.Key)), StringComparer.Ordinal);

        var template = Encoding.UTF8.GetString(indexTemplate);
        _index = Languages.ToFrozenDictionary(
            language => language,
            language => new StaticAsset(Encoding.UTF8.GetBytes(RenderIndex(template, language, options.Value)), ContentTypes[".html"], varyByLanguage: true),
            StringComparer.Ordinal);
    }

    /// <summary>Supported UI languages; the first is the fallback.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "th"];

    public IEnumerable<string> AssetNames => _assets.Keys;

    public static string Version { get; } = ReadVersion();

    public Task ServeIndexAsync(HttpContext context) => _index[PickLanguage(context.Request)].WriteAsync(context);

    public Task ServeAssetAsync(HttpContext context, string name) => _assets[name].WriteAsync(context);

    /// <summary>The first supported language in the visitor's Accept-Language preference order.</summary>
    public static string PickLanguage(HttpRequest request)
    {
        var preferences = request.GetTypedHeaders().AcceptLanguage
            .Where(value => value.Quality is null or > 0)
            .OrderByDescending(value => value.Quality ?? 1); // stable: equal weights keep the browser's order
        foreach (var preference in preferences)
        {
            var tag = preference.Value.Value ?? string.Empty;
            var match = Languages.FirstOrDefault(language =>
                tag.Equals(language, StringComparison.OrdinalIgnoreCase) || tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Languages[0];
    }

    private static string RenderIndex(string template, string language, ServerOptions options) => template
        .Replace("{{lang}}", language, StringComparison.Ordinal)
        .Replace("{{version}}", WebUtility.HtmlEncode(Version), StringComparison.Ordinal)
        .Replace("{{maxRequestBytes}}", options.MaxRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{{maxImagePixels}}", options.MaxImagePixels.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ContentTypeOf(string name) =>
        ContentTypes.TryGetValue(Path.GetExtension(name), out var type) ? type : "application/octet-stream";

    private static string ReadVersion()
    {
        var version = typeof(WebUi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    private sealed class StaticAsset(byte[] content, string contentType, bool varyByLanguage = false)
    {
        private readonly string _etag = "\"" + Convert.ToBase64String(SHA256.HashData(content).AsSpan(0, 12)).Replace('+', '-').Replace('/', '_') + "\"";

        public async Task WriteAsync(HttpContext context)
        {
            var response = context.Response;
            response.Headers.ETag = _etag;
            // Revalidate every time: costs a 304 on a LAN, and a new version is never hidden behind a stale cache.
            response.Headers.CacheControl = "no-cache";
            if (varyByLanguage)
            {
                response.Headers.Vary = HeaderNames.AcceptLanguage;
            }

            var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
            if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || string.Equals(tag.Tag.Value, _etag, StringComparison.Ordinal)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            response.ContentType = contentType;
            response.ContentLength = content.Length;
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await response.Body.WriteAsync(content, context.RequestAborted);
            }
        }
    }
}
