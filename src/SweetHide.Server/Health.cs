using Microsoft.Extensions.Diagnostics.HealthChecks;

using SweetHide.Core.Imaging;

namespace SweetHide.Server;

/// <summary>
/// Healthy when SkiaSharp's native library loads and round-trips a PNG, which is what breaks when an image is built
/// for the wrong architecture or C library.
/// </summary>
internal sealed class ImageCodecHealthCheck(IImageCodec codec) : IHealthCheck
{
    private static readonly byte[] Pixel = [255, 105, 180, 255];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        codec.EncodePng(new PixelBuffer(1, 1, (byte[])Pixel.Clone()), stream);
        stream.Position = 0;
        var decoded = codec.Decode(stream, applyOrientation: false);
        return Task.FromResult(decoded.Pixels.SequenceEqual(Pixel)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("A PNG round trip changed the pixel."));
    }
}

/// <summary>
/// <c>SweetHide.Server --healthcheck [url]</c>: probes /healthz and exits 0 or 1. The chiseled container image has no
/// shell or curl, so its HEALTHCHECK runs the app itself in this mode.
/// </summary>
internal static class HealthProbe
{
    public const string Argument = "--healthcheck";

    public static async Task<int> RunAsync(string? url)
    {
        var target = new Uri(url ?? DefaultUrl());
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(target);
            await Console.Out.WriteLineAsync($"{(int)response.StatusCode} {response.ReasonPhrase}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Out.WriteLineAsync($"unhealthy: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The first port in ASPNETCORE_HTTP_PORTS (what the container listens on), else 8080.</summary>
    internal static string DefaultUrl()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? string.Empty;
        var port = ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return $"http://127.0.0.1:{port ?? ServerApp.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}{ServerApp.HealthPath}";
    }
}
