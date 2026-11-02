using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Options;

using static SweetHide.Server.Tests.Http;

namespace SweetHide.Server.Tests;

public sealed class SecurityTests
{
    private const string Token = "correct-horse-battery-staple-42";

    [Fact]
    public async Task Requests_over_the_size_limit_get_413()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:MaxRequestBytes"] = "65536" });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/hide", Form(
            ("cover", TestImages.PhotoPng(40, 40), "c.png"),
            ("file", TestImages.RandomBytes(100_000), "big.bin"),
            ("password", ServerFactory.Password, null)), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("request-too-large", problem.Code());
        Assert.Equal(65536, problem.GetProperty("limitBytes").GetInt64());
    }

    [Fact]
    public async Task Each_client_is_rate_limited_but_the_health_check_is_not()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:RateLimitPerMinute"] = "3" });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await client.GetAsync("/app.css", Ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var limited = await client.GetAsync("/app.css", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate-limited", (await ProblemAsync(limited)).Code());
        Assert.InRange(limited.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 60);

        using var limitedApi = await client.PostAsync("/api/capacity", Form(("image", TestImages.PhotoPng(8, 8), "c.png")), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedApi.StatusCode);

        using var health = await client.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Access_token_is_required_when_configured()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:AccessToken"] = Token });
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("unauthorized", (await ProblemAsync(anonymous)).Code());

        using var anonymousApi = await client.PostAsync("/api/capacity", Form(("image", TestImages.PhotoPng(8, 8), "c.png")), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(client, new AuthenticationHeaderValue("Bearer", "wrong-token")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(client, Basic("me", "wrong-token")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(client, new AuthenticationHeaderValue("Basic", "not base64!")));
        Assert.Equal(HttpStatusCode.OK, await StatusWith(client, new AuthenticationHeaderValue("Bearer", Token)));
        Assert.Equal(HttpStatusCode.OK, await StatusWith(client, Basic("anyone", Token)));

        using var health = await client.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Api_works_with_a_bearer_token()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:AccessToken"] = Token });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await client.PostAsync("/api/capacity", Form(("image", TestImages.PhotoPng(8, 8), "c.png")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/nope")]
    [InlineData("/healthz")]
    public async Task Security_headers_are_on_every_response(string path)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Api_errors_are_not_cached_and_carry_security_headers()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/reveal", Form(("image", TestImages.PhotoPng(40, 40), "c.png"), ("password", "x", null)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // Errors from exceptions go through a handler that clears the response first; the headers must survive that.
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    [InlineData("none", HttpStatusCode.OK)]
    public async Task Browsers_cannot_call_the_api_from_other_sites(string fetchSite, HttpStatusCode expected)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/capacity")
        {
            Content = Form(("image", TestImages.PhotoPng(8, 8), "c.png")),
        };
        request.Headers.Add("Sec-Fetch-Site", fetchSite);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Job_limiter_answers_503_when_the_queue_is_full()
    {
        var options = Options.Create(new ServerOptions { MaxConcurrentJobs = 1, MaxQueuedJobs = 0 });
        using var limiter = new JobLimiter(options);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first = Task.Run(() => limiter.RunAsync(
            () =>
            {
                started.Set();
                release.Wait(Ct);
                return 1;
            },
            Ct));
        started.Wait(Ct);

        var busy = await Assert.ThrowsAsync<ApiProblemException>(() => limiter.RunAsync(() => 2, Ct));
        Assert.Equal(503, busy.Status);
        Assert.Equal("busy", busy.Code);
        Assert.Equal(5, busy.RetryAfterSeconds);

        release.Set();
        Assert.Equal(1, await first);
        Assert.Equal(3, await limiter.RunAsync(() => 3, Ct));
    }

    [Theory]
    [InlineData("MaxRequestBytes", "10")]
    [InlineData("MaxConcurrentJobs", "0")]
    [InlineData("RateLimitPerMinute", "0")]
    [InlineData("TrustedProxies:0", "not-an-ip")]
    public async Task Invalid_settings_stop_the_server_from_starting(string key, string value)
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { [$"SweetHide:{key}"] = value });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(key.Split(':')[0], error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_proxies_accept_addresses_and_ranges()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?>
        {
            ["SweetHide:TrustedProxies:0"] = "10.0.0.5",
            ["SweetHide:TrustedProxies:1"] = "172.16.0.0/12",
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static async Task<HttpStatusCode> StatusWith(HttpClient client, AuthenticationHeaderValue authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Authorization = authorization;
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
