using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;

namespace SweetHide.Server;

internal static class RateLimiting
{
    /// <summary>Endpoint policy for /api: caps requests in flight (uploading, queued or working) across all clients.</summary>
    public const string ApiPolicy = "api";

    private const int BusyRetryAfterSeconds = 5;

    public static void Configure(RateLimiterOptions limiter, ServerOptions options)
    {
        // Per client IP, every request but the health check: protects the access token from guessing and the box from
        // one noisy client. Behind a reverse proxy the IP comes from X-Forwarded-For (see TrustedProxies).
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Request.Path.StartsWithSegments(ServerApp.HealthPath, StringComparison.Ordinal)
                ? RateLimitPartition.GetNoLimiter("health")
                : RateLimitPartition.GetFixedWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

        // Uploads are held in memory, so the number of API requests in flight is bounded too: the jobs that may run
        // plus the ones that may wait for them (JobLimiter).
        limiter.AddConcurrencyLimiter(ApiPolicy, concurrency =>
        {
            concurrency.PermitLimit = options.MaxConcurrentJobs + options.MaxQueuedJobs;
            concurrency.QueueLimit = 0;
        });

        limiter.OnRejected = OnRejectedAsync;
    }

    internal static string ClientKey(IPAddress? address) => address switch
    {
        null => "unknown",
        { IsIPv4MappedToIPv6: true } => address.MapToIPv4().ToString(),
        _ => address.ToString(),
    };

    private static async ValueTask OnRejectedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;

        // Only the per-IP window reports when it reopens; the in-flight cap has no such time.
        var rateLimited = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);
        var seconds = rateLimited ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : BusyRetryAfterSeconds;
        var problem = rateLimited
            ? ProblemExceptionHandler.Create(StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, "Too many requests. Slow down a little.")
            : ProblemExceptionHandler.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.Busy, "The server is busy. Try again in a moment.");

        context.Response.StatusCode = problem.Status!.Value;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
