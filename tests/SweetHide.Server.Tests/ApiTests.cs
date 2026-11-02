using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using SweetHide.Core;

using static SweetHide.Server.Tests.Http;

namespace SweetHide.Server.Tests;

public sealed class ApiTests
{
    private sealed record RevealedFileDto(string Name, long Size, byte[] Data);

    private sealed record RevealDto(List<string> Messages, List<RevealedFileDto> Files);

    private sealed record CapacityLevelDto(int BitsPerChannel, long ContainerBytes, long PayloadBytes);

    private sealed record CapacityDto(int Width, int Height, long UsablePixels, int OverheadBytes, List<CapacityLevelDto> Levels);

    private sealed record ChannelDto(string Channel, double OnesRatio, double? ChiSquare, int DegreesOfFreedom, double? EmbeddingProbability);

    private sealed record BitPlaneDto(int Bit, byte[] Png);

    private sealed record AnalyzeDto(int Width, int Height, List<ChannelDto> Channels, BitPlaneDto? BitPlane);

    [Fact]
    public async Task Hide_then_reveal_round_trips_messages_and_files()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var data = TestImages.RandomBytes(2000);

        using var hide = await client.PostAsync("/api/hide", Form(
            ("cover", TestImages.PhotoPng(200, 150), "ทะเล.png"),
            ("text", "สวัสดี homelab", null),
            ("text", "second message", null),
            ("file", "รายละเอียดลับ"u8.ToArray(), "แผนงาน.txt"),
            ("file", data, "data.bin"),
            ("password", ServerFactory.Password, null),
            ("bits", 2, null)), Ct);

        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        Assert.Equal("image/png", hide.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", hide.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("ทะเล.png", hide.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("no-store", hide.Headers.CacheControl?.ToString());
        var stego = await hide.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(200, TestImages.Decode(stego).Width);

        using var reveal = await client.PostAsync("/api/reveal", Form(("image", stego, "ทะเล.png"), ("password", ServerFactory.Password, null)), Ct);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        var result = await JsonAsync<RevealDto>(reveal);
        Assert.Equal(["สวัสดี homelab", "second message"], result.Messages);
        Assert.Collection(
            result.Files,
            file =>
            {
                Assert.Equal("แผนงาน.txt", file.Name);
                Assert.Equal("รายละเอียดลับ"u8.ToArray(), file.Data);
                Assert.Equal(file.Data.Length, file.Size);
            },
            file =>
            {
                Assert.Equal("data.bin", file.Name);
                Assert.Equal(data, file.Data);
            });
    }

    [Fact]
    public async Task Reveal_sanitizes_file_names()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var stego = await HideAsync(client, TestImages.PhotoPng(120, 90), ("file", "x"u8.ToArray(), "../../etc/CON.txt"));
        using var reveal = await client.PostAsync("/api/reveal", Form(("image", stego, "s.png"), ("password", ServerFactory.Password, null)), Ct);

        var file = Assert.Single((await JsonAsync<RevealDto>(reveal)).Files);
        Assert.Equal("_CON.txt", file.Name);
    }

    [Fact]
    public async Task Reveal_as_zip_contains_files_and_messages_txt()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var stego = await HideAsync(
            client,
            TestImages.PhotoPng(200, 150),
            ("text", "first", null),
            ("text", "ข้อความที่สอง", null),
            ("file", "a"u8.ToArray(), "notes.txt"),
            ("file", "b"u8.ToArray(), "notes.txt"),
            ("file", "c"u8.ToArray(), "messages.txt"));

        using var response = await client.PostAsync("/api/reveal?format=zip", Form(("image", stego, "holiday.png"), ("password", ServerFactory.Password, null)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("holiday.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct), ZipArchiveMode.Read);
        Assert.Equal(["messages.txt", "notes.txt", "notes (1).txt", "messages (1).txt"], zip.Entries.Select(e => e.FullName));
        Assert.Equal("===== 1 =====\nfirst\n\n===== 2 =====\nข้อความที่สอง", ReadEntry(zip, "messages.txt"));
        Assert.Equal("b", ReadEntry(zip, "notes (1).txt"));
        Assert.Equal("c", ReadEntry(zip, "messages (1).txt"));
    }

    [Fact]
    public async Task Unknown_reveal_format_is_rejected()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/reveal?format=tar", Form(("image", TestImages.PhotoPng(40, 40), "a.png"), ("password", "x", null)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-field", (await ProblemAsync(response)).Code());
    }

    [Fact]
    public async Task Wrong_password_and_no_data_give_the_same_422()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var cover = TestImages.PhotoPng(160, 120);
        var stego = await HideAsync(client, cover, ("text", "secret", null));

        using var wrong = await client.PostAsync("/api/reveal", Form(("image", stego, "s.png"), ("password", "not the password", null)), Ct);
        using var plain = await client.PostAsync("/api/reveal", Form(("image", cover, "c.png"), ("password", ServerFactory.Password, null)), Ct);

        foreach (var response in new[] { wrong, plain })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await ProblemAsync(response);
            Assert.Equal("not-found-or-wrong-password", problem.Code());
            Assert.Equal("Nothing found, or the password is wrong.", problem.GetProperty("title").GetString());
            Assert.False(problem.TryGetProperty("detail", out _));
        }
    }

    [Fact]
    public async Task Data_that_does_not_fit_returns_422_with_sizes_and_a_suggestion()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        // 40x40 holds a bit under 600 bytes at 1 bit and twice that at 2 bits; 700 random bytes don't compress.
        var cover = TestImages.PhotoPng(40, 40);
        var oneBit = StegoEngine.GetCapacity(TestImages.Decode(cover), 1).ContainerBytes;
        using var response = await client.PostAsync("/api/hide", Form(
            ("cover", cover, "small.png"),
            ("file", TestImages.RandomBytes(700), "noise.bin"),
            ("password", ServerFactory.Password, null)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("insufficient-capacity", problem.Code());
        Assert.Equal(oneBit, problem.GetProperty("capacityBytes").GetInt64());
        Assert.True(problem.GetProperty("neededBytes").GetInt64() > 700);
        Assert.Equal(1, problem.GetProperty("bitsPerChannel").GetInt32());
        Assert.Equal(2, problem.GetProperty("suggestedBitsPerChannel").GetInt32());
    }

    [Fact]
    public async Task Capacity_reports_every_bit_depth()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var cover = TestImages.PhotoPng(40, 40);
        using var response = await client.PostAsync("/api/capacity", Form(("image", cover, "c.png")), Ct);

        // The server must report exactly what the engine computes; the numbers depend on the format version.
        var image = TestImages.Decode(cover);
        var expected = StegoEngine.SupportedBitsPerChannel.Select(bits => StegoEngine.GetCapacity(image, bits).ContainerBytes);
        var capacity = await JsonAsync<CapacityDto>(response);
        Assert.Equal((40, 40, 1600L, StegoEngine.ContainerOverhead), (capacity.Width, capacity.Height, capacity.UsablePixels, capacity.OverheadBytes));
        Assert.Equal([1, 2, 3, 4], capacity.Levels.Select(l => l.BitsPerChannel));
        Assert.Equal(expected, capacity.Levels.Select(l => l.ContainerBytes));
        Assert.All(capacity.Levels, l => Assert.Equal(l.ContainerBytes - StegoEngine.ContainerOverhead, l.PayloadBytes));
        Assert.True(capacity.Levels[0].ContainerBytes is > 500 and < 700, "the 700-byte test in Data_that_does_not_fit_returns_422_with_sizes_and_a_suggestion relies on this");
    }

    [Fact]
    public async Task Analyze_returns_channel_statistics_and_the_requested_bit_plane()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/analyze", Form(("image", TestImages.PhotoPng(64, 48), "a.png"), ("bitPlane", 3, null)), Ct);

        var report = await JsonAsync<AnalyzeDto>(response);
        Assert.Equal((64, 48), (report.Width, report.Height));
        Assert.Equal(["R", "G", "B"], report.Channels.Select(c => c.Channel));
        Assert.All(report.Channels, c => Assert.InRange(c.OnesRatio, 0, 1));
        Assert.Equal(3, report.BitPlane?.Bit);
        Assert.Equal(64, TestImages.Decode(report.BitPlane!.Png).Width);
    }

    [Fact]
    public async Task Analyze_without_bit_plane_returns_only_statistics()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/analyze", Form(("image", TestImages.PhotoPng(32, 32), "a.png")), Ct);

        Assert.Null((await JsonAsync<AnalyzeDto>(response)).BitPlane);
    }

    [Theory]
    [InlineData("password", "missing-field")]
    [InlineData("bits", "invalid-field")]
    [InlineData("text", "nothing-to-hide")]
    public async Task Invalid_hide_requests_return_400_with_a_code(string broken, string code)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var fields = new List<(string, object, string?)> { ("cover", TestImages.PhotoPng(40, 40), "c.png") };
        if (broken != "password")
        {
            fields.Add(("password", ServerFactory.Password, null));
        }

        if (broken != "text")
        {
            fields.Add(("text", "hello", null));
        }

        if (broken == "bits")
        {
            fields.Add(("bits", 5, null));
        }

        using var response = await client.PostAsync("/api/hide", Form([.. fields]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(code, problem.Code());
        Assert.False(problem.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task Something_that_is_not_an_image_returns_422()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/capacity", Form(("image", "just text"u8.ToArray(), "notes.png")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid-image", (await ProblemAsync(response)).Code());
    }

    [Fact]
    public async Task Images_over_the_pixel_limit_are_refused()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:MaxImagePixels"] = "1000" });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/capacity", Form(("image", TestImages.PhotoPng(40, 40), "big.png")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("invalid-image", problem.Code());
        Assert.Contains("the limit is", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_multipart_form_data_is_accepted()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/capacity", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported-media-type", (await ProblemAsync(response)).Code());
    }

    [Fact]
    public async Task Unknown_routes_and_methods_return_problem_json()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var missing = await client.GetAsync("/nope", Ct);
        using var wrongMethod = await client.GetAsync("/api/hide", Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not-found", (await ProblemAsync(missing)).Code());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("application/problem+json", wrongMethod.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_check_reports_healthy()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Uploads_are_buffered_in_memory_up_to_the_request_limit()
    {
        await using var factory = new ServerFactory(new Dictionary<string, string?> { ["SweetHide:MaxRequestBytes"] = "10485760" });

        var form = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        // Below this threshold ASP.NET Core never writes a multipart section to a temporary file.
        Assert.Equal(10485760, form.MemoryBufferThreshold);
        Assert.Equal(10485760, form.MultipartBodyLengthLimit);
        Assert.Equal(10485760, form.ValueLengthLimit);
    }

    private static async Task<byte[]> HideAsync(HttpClient client, byte[] cover, params (string Name, object Value, string? FileName)[] items)
    {
        using var response = await client.PostAsync(
            "/api/hide",
            Form([("cover", cover, "cover.png"), .. items, ("password", ServerFactory.Password, null)]),
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
