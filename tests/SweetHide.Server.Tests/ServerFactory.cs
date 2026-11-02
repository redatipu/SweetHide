using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;

namespace SweetHide.Server.Tests;

/// <summary>
/// The real app on an in-memory test server, as it runs in production except for a cheap Argon2. The engine is
/// swapped through ConfigureTestServices, which only exists in the test host: the app itself has no setting for it.
/// </summary>
internal sealed class ServerFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string Password = "homelab-test-pass";

    /// <summary>Cheap Argon2 settings so tests run fast. Never use outside tests.</summary>
    public static readonly KdfParameters FastKdf = new(MemoryKiB: 64, Iterations: 1, Parallelism: 1);

    private static readonly Dictionary<string, string?> Defaults = new()
    {
        // High enough that only the rate-limit test ever hits it.
        ["SweetHide:RateLimitPerMinute"] = "100000",

        // Every test starts its own host; only errors are worth printing.
        ["Logging:LogLevel:Default"] = "Error",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Error",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(Defaults);
            config.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        });
        builder.ConfigureTestServices(services => services.AddSingleton(new StegoEngine(FastKdf)));
    }
}

internal static class Http
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static MultipartFormDataContent Form(params (string Name, object Value, string? FileName)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value, fileName) in fields)
        {
            switch (value)
            {
                case byte[] bytes:
                    var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(content, name, fileName ?? "file");
                    break;
                default:
                    form.Add(new StringContent(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!), name);
                    break;
            }
        }

        return form;
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    public static async Task<T> JsonAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, Ct))!;
    }

    public static string Code(this JsonElement problem) => problem.GetProperty("code").GetString()!;
}

internal static class TestImages
{
    private static readonly SkiaImageCodec Codec = new();

    /// <summary>A photo-like PNG: smooth gradients plus mild noise, so LSBs look natural.</summary>
    public static byte[] PhotoPng(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                data[i] = Clamp((x * 255 / Math.Max(1, width - 1)) + random.Next(-6, 7));
                data[i + 1] = Clamp((y * 255 / Math.Max(1, height - 1)) + random.Next(-6, 7));
                data[i + 2] = Clamp(128 + (int)(60 * Math.Sin((x + y) / 9.0)) + random.Next(-6, 7));
                data[i + 3] = 255;
            }
        }

        using var stream = new MemoryStream();
        Codec.EncodePng(new PixelBuffer(width, height, data), stream);
        return stream.ToArray();
    }

    public static PixelBuffer Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return Codec.Decode(stream, applyOrientation: false);
    }

    public static byte[] RandomBytes(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
