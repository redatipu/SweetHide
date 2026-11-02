using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

namespace SweetHide.Server;

/// <summary>
/// Caps how many image jobs (decoding, Argon2 at 64 MiB, embedding, encoding) run at once, so a few requests can't
/// exhaust the memory or CPU of a small homelab box. Callers beyond the queue get 503.
/// </summary>
internal sealed class JobLimiter : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;

    public JobLimiter(IOptions<ServerOptions> options)
    {
        var value = options.Value;
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = value.MaxConcurrentJobs,
            QueueLimit = value.MaxQueuedJobs,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>Waits for a slot, then runs <paramref name="job"/> synchronously on the calling thread.</summary>
    /// <exception cref="ApiProblemException">The queue is full (503).</exception>
    public async Task<T> RunAsync<T>(Func<T> job, CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw Busy();
        }

        // The work itself can't be interrupted (Argon2 has no cancellation), so the slot is held until it finishes.
        return job();
    }

    public void Dispose() => _limiter.Dispose();

    public static ApiProblemException Busy() => new(
        StatusCodes.Status503ServiceUnavailable,
        ProblemCodes.Busy,
        "The server is busy. Try again in a moment.")
    {
        RetryAfterSeconds = 5,
    };
}
