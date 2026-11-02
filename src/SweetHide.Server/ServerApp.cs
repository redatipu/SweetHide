using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Server;

/// <summary>Builds the web application: services, limits and the middleware pipeline.</summary>
internal static partial class ServerApp
{
    public const string ApiPrefix = "/api";
    public const string HealthPath = "/healthz";
    public const int DefaultPort = 8080;

    public static WebApplication Build(string[] args)
    {
        EnsureUnicodeNormalization();

        var builder = WebApplication.CreateBuilder(args);
        UseDefaultPort(builder);
        ConfigureServices(builder.Services);

        var app = builder.Build();
        Configure(app);
        return app;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();

        // Always the format's real Argon2 cost; there is deliberately no setting for it. Tests swap in a cheaper engine
        // with ConfigureTestServices, which only exists in the test host.
        services.AddSingleton(_ => new StegoEngine());
        services.AddSingleton<IImageCodec>(sp => new SkiaImageCodec(sp.GetRequiredService<IOptions<ServerOptions>>().Value.MaxImagePixels));
        services.AddSingleton<JobLimiter>();
        services.AddSingleton<WebUi>();

        // Thai text stays readable in JSON; characters that matter in HTML are still escaped.
        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

        services.AddProblemDetails(problems => problems.CustomizeProblemDetails = context =>
        {
            // Bare status codes (404, 405, ...) get a code too, so clients can always switch on "code".
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
            {
                StatusCodes.Status404NotFound => ProblemCodes.NotFound,
                StatusCodes.Status413PayloadTooLarge => ProblemCodes.RequestTooLarge,
                StatusCodes.Status500InternalServerError => ProblemCodes.InternalError,
                _ => ProblemCodes.BadRequest,
            });
        });
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.AddHealthChecks().AddCheck<ImageCodecHealthCheck>("image-codec");

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ServerOptions>>((limiter, options) => RateLimiting.Configure(limiter, options.Value));

        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<ServerOptions>>((timeouts, options) =>
            timeouts.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds),
                TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
                WriteTimeoutResponse = WriteTimeoutAsync,
            });

        services.AddOptions<KestrelServerOptions>().Configure<IOptions<ServerOptions>>((kestrel, options) =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.Value.MaxRequestBytes;
        });

        services.AddOptions<FormOptions>().Configure<IOptions<ServerOptions>>((form, options) =>
        {
            // Uploads stay in memory: with the buffering threshold at the request limit, nothing is ever spilled to a
            // temporary file. Text fields may be as long as the request allows.
            var limit = (int)options.Value.MaxRequestBytes;
            form.MemoryBufferThreshold = limit;
            form.MultipartBodyLengthLimit = limit;
            form.BufferBodyLengthLimit = limit;
            form.ValueLengthLimit = limit;
        });

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ServerOptions>>((forwarded, options) =>
        {
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in options.Value.TrustedProxies)
            {
                if (ServerOptions.TryParseProxy(proxy, out var address, out var network))
                {
                    if (address is not null)
                    {
                        forwarded.KnownProxies.Add(address);
                    }
                    else
                    {
                        forwarded.KnownIPNetworks.Add(network!.Value);
                    }
                }
            }

            forwarded.ForwardedHeaders = options.Value.TrustedProxies.Count > 0
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.None;
        });
    }

    private static void Configure(WebApplication app)
    {
        // Registered first so every response gets the headers, including errors and rejections.
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseMiddleware<AccessTokenMiddleware>();
        app.UseMiddleware<CrossSiteGuardMiddleware>();
        app.UseRequestTimeouts();

        app.MapHealthChecks(HealthPath);
        app.MapStegoApi();
        app.MapWebUi();

        LogStartup(app.Logger, app.Services.GetRequiredService<IOptions<ServerOptions>>().Value);
    }

    private static void MapWebUi(this IEndpointRouteBuilder endpoints)
    {
        var ui = endpoints.ServiceProvider.GetRequiredService<WebUi>();
        string[] methods = [HttpMethods.Get, HttpMethods.Head];
        endpoints.MapMethods("/", methods, ui.ServeIndexAsync);
        endpoints.MapMethods("/" + WebUi.IndexFile, methods, ui.ServeIndexAsync);
        foreach (var name in ui.AssetNames)
        {
            endpoints.MapMethods("/" + name, methods, (HttpContext context) => ui.ServeAssetAsync(context, name));
        }
    }

    /// <summary>
    /// Port 8080 unless something else was configured (ASPNETCORE_HTTP_PORTS, ASPNETCORE_URLS, --urls or Kestrel
    /// endpoints). The official container images already set ASPNETCORE_HTTP_PORTS=8080.
    /// </summary>
    private static void UseDefaultPort(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        if (string.IsNullOrEmpty(config[WebHostDefaults.ServerUrlsKey])
            && string.IsNullOrEmpty(config[WebHostDefaults.HttpPortsKey])
            && string.IsNullOrEmpty(config[WebHostDefaults.HttpsPortsKey])
            && !config.GetSection("Kestrel:Endpoints").Exists())
        {
            builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, DefaultPort.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Passwords are NFC-normalized before key derivation (docs/FORMAT.md). Without ICU (.NET's globalization-invariant
    /// mode) Normalize() silently does nothing, so refuse to start rather than make incompatible images.
    /// </summary>
    private static void EnsureUnicodeNormalization()
    {
        if (!string.Equals("é".Normalize(NormalizationForm.FormC), "é", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unicode normalization is not available (globalization-invariant mode or ICU missing). Passwords would not " +
                "match the other SweetHide apps. Use an image with ICU, e.g. mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra.");
        }
    }

    private static Task WriteTimeoutAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ProblemExceptionHandler.Create(StatusCodes.Status504GatewayTimeout, ProblemCodes.Timeout, "The request took too long."),
        }).AsTask();

    private static void LogStartup(ILogger logger, ServerOptions options)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            var maxRequest = ByteSize.Format(options.MaxRequestBytes);
            LogSettings(
                logger,
                WebUi.Version,
                maxRequest,
                options.MaxImagePixels,
                options.MaxConcurrentJobs,
                options.MaxQueuedJobs,
                options.RateLimitPerMinute,
                options.RequestTimeoutSeconds,
                options.RequiresAccessToken ? "required" : "off",
                options.TrustedProxies.Count);
        }

        if (!options.RequiresAccessToken)
        {
            LogNoToken(logger);
        }
        else if (options.AccessToken!.Length < 16)
        {
            LogShortToken(logger);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "SweetHide server {Version}: requests up to {MaxRequest}, images up to {MaxPixels} pixels, {Jobs} concurrent job(s) " +
            "with {Queue} queued, {Rate} requests/min per IP, {Timeout}s timeout, access token {Token}, {Proxies} trusted proxy entr(ies)")]
    private static partial void LogSettings(
        ILogger logger, string version, string maxRequest, long maxPixels, int jobs, int queue, int rate, int timeout, string token, int proxies);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No access token is set: anyone who can reach this port can use the server. Set SWEETHIDE__ACCESSTOKEN to require one.")]
    private static partial void LogNoToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The access token is shorter than 16 characters; a long random one is much harder to guess.")]
    private static partial void LogShortToken(ILogger logger);
}
