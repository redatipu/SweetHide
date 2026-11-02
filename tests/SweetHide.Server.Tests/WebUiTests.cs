using System.Net;
using System.Text.RegularExpressions;

using static SweetHide.Server.Tests.Http;

namespace SweetHide.Server.Tests;

public sealed partial class WebUiTests
{
    [Theory]
    [InlineData("th-TH,th;q=0.9,en;q=0.8", "th")]
    [InlineData("en-US,en;q=0.9,th;q=0.8", "en")]
    [InlineData("fr-FR, th;q=0.5", "th")]
    [InlineData("en;q=0.4, th;q=0.6", "th")]
    [InlineData("de-DE", "en")]
    [InlineData(null, "en")]
    public async Task Index_is_served_in_the_visitors_language(string? acceptLanguage, string expected)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept-Language", response.Headers.Vary);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith($"<!doctype html>\n<html lang=\"{expected}\"", html.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_carries_the_server_limits_and_version()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:MaxRequestBytes"] = "1048576" });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/", Ct);

        Assert.Contains("data-max-request-bytes=\"1048576\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_works_with_the_strict_content_security_policy()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/", Ct);

        // No inline script, inline style, event handler attribute or external resource, or the CSP would block it.
        Assert.DoesNotMatch(InlineScript(), html);
        Assert.DoesNotMatch(StyleAttributeOrElement(), html);
        Assert.DoesNotMatch(EventHandlerAttribute(), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(ExternalUrl(), html);
    }

    [Theory]
    [InlineData("/app.js", "text/javascript")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    [InlineData("/manifest.webmanifest", "application/manifest+json")]
    [InlineData("/index.html", "text/html")]
    public async Task Assets_are_served_with_types_and_revalidated_with_etags(string path, string contentType)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(contentType, first.Content.Headers.ContentType?.MediaType);
        Assert.True(first.Headers.CacheControl?.NoCache);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(etag);
        using var second = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Head_requests_get_headers_without_a_body()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/app.js"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Ui_script_has_both_languages_for_every_key()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/app.js", Ct);
        var html = await client.GetStringAsync("/", Ct);

        var english = Keys(Block(script, "en"));
        var thai = Keys(Block(script, "th"));
        Assert.NotEmpty(english);
        Assert.Equal(english.Order(StringComparer.Ordinal), thai.Order(StringComparer.Ordinal));
        foreach (Match match in I18nAttribute().Matches(html))
        {
            Assert.Contains(match.Groups["key"].Value, english);
        }
    }

    private static string Block(string script, string language)
    {
        var start = script.IndexOf($"    {language}: {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {language} strings");
        var end = script.IndexOf("\n    },", start, StringComparison.Ordinal);
        return script[start..end];
    }

    private static HashSet<string> Keys(string block) =>
        [.. StringKey().Matches(block).Select(m => m.Groups["key"].Value)];

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<style\b|\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttributeOrElement();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex(@"(src|href)\s*=\s*""(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    [GeneratedRegex(@"data-i18n(?:-[a-z]+)?=""(?<key>[A-Za-z0-9]+)""")]
    private static partial Regex I18nAttribute();

    [GeneratedRegex(@"^\s{6}(?<key>[A-Za-z0-9]+):", RegexOptions.Multiline)]
    private static partial Regex StringKey();
}
