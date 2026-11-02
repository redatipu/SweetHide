using System.Net;
using System.Security.Cryptography;
using System.Text;

using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

namespace SweetHide.Presentation.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly byte[] ZipBytes = Encoding.ASCII.GetBytes("pretend this is a zip");
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-update-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Theory]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3-beta.1", 1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1.1", -1)]
    [InlineData("1.2.3+build.5", "1.2.3", 0)]
    public void Semantic_version_precedence(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(SemanticVersion.Compare(left, right)));

    [Fact]
    public void Finds_checksums_in_sha256sum_format()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.zip\n{hash}  SweetHide-1.0.0-win-x64.zip\n{new string('c', 64)} *binary-mode.zip\n";
        Assert.Equal(hash, Checksums.Find(sums, "SweetHide-1.0.0-win-x64.zip"));
        Assert.Equal(new string('c', 64), Checksums.Find(sums, "binary-mode.zip"));
        Assert.Null(Checksums.Find(sums, "missing.zip"));
        Assert.Null(Checksums.Find("garbage line\n", "garbage"));
    }

    [Fact]
    public void Portable_mode_keeps_data_next_to_the_app()
    {
        var app = _dir.CreateSubdirectory("app").FullName;
        var normal = AppPaths.Resolve(app, localAppData: Path.Combine(_dir.FullName, "local"));
        Assert.False(normal.IsPortable);
        Assert.Equal(Path.Combine(_dir.FullName, "local", "SweetHide", "settings.json"), normal.SettingsFile);

        File.WriteAllText(Path.Combine(app, AppPaths.PortableMarker), string.Empty);
        var portable = AppPaths.Resolve(app);
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(app, "data", "settings.json"), portable.SettingsFile);
    }

    [Fact]
    public async Task Finds_a_newer_release_and_downloads_it_with_a_verified_checksum()
    {
        var service = new GitHubUpdateService(new HttpClient(new FakeGitHub(Hash(ZipBytes))));

        Assert.Null(await service.CheckAsync("2.0.0", TestContext.Current.CancellationToken));
        var update = await service.CheckAsync("0.1.0", TestContext.Current.CancellationToken);
        Assert.NotNull(update);
        Assert.Equal("1.0.0", update.Version);
        Assert.Equal("SweetHide-1.0.0-win-x64.zip", update.AssetName);

        var path = await service.DownloadAsync(update, _dir.FullName, TestContext.Current.CancellationToken);
        Assert.Equal(ZipBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_tampered_download_is_deleted()
    {
        var service = new GitHubUpdateService(new HttpClient(new FakeGitHub(new string('0', 64))));
        var update = await service.CheckAsync("0.1.0", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(update!, _dir.FullName, TestContext.Current.CancellationToken));
        Assert.Empty(_dir.GetFiles());
    }

    [Fact]
    public async Task Settings_page_checks_downloads_and_opens_the_release()
    {
        var shell = new FakeShell();
        var service = new GitHubUpdateService(new HttpClient(new FakeGitHub(Hash(ZipBytes))));
        var vm = new SettingsViewModel(new MemorySettingsStore(), new FakeTheme(), new AppSettings(), service, shell, _dir.FullName);

        Assert.False(vm.CheckForUpdatesOnStart);
        await vm.CheckOnStartupAsync();
        Assert.False(vm.HasUpdate); // off by default: no network at start-up

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.HasUpdate);
        Assert.Contains("1.0.0", vm.UpdateBanner, StringComparison.Ordinal);

        await vm.DownloadUpdateCommand.ExecuteAsync(null);
        Assert.True(File.Exists(vm.DownloadedUpdatePath));
        Assert.Equal([vm.DownloadedUpdatePath!], shell.Shown);

        vm.OpenReleasePageCommand.Execute(null);
        Assert.Equal("https://github.com/redatipu/SweetHide/releases/tag/v1.0.0", shell.Opened.Single().ToString());
    }

    [Fact]
    public async Task Offline_check_reports_a_friendly_error()
    {
        var vm = new SettingsViewModel(new MemorySettingsStore(), new FakeTheme(), new AppSettings(), new GitHubUpdateService(new HttpClient(new Offline())));
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.HasUpdate);
        Assert.NotEmpty(vm.UpdateStatus);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed class FakeGitHub(string zipHash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("SweetHide", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            var url = request.RequestUri!.ToString();
            HttpContent content = url switch
            {
                _ when url.EndsWith("/releases/latest", StringComparison.Ordinal) => new StringContent("""
                    {
                      "tag_name": "v1.0.0",
                      "html_url": "https://github.com/redatipu/SweetHide/releases/tag/v1.0.0",
                      "draft": false,
                      "prerelease": false,
                      "assets": [
                        { "name": "SweetHide-1.0.0-win-arm64.zip", "browser_download_url": "https://example.test/arm.zip" },
                        { "name": "SweetHide-1.0.0-win-x64.zip", "browser_download_url": "https://example.test/x64.zip" },
                        { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt" }
                      ]
                    }
                    """),
                _ when url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal) => new StringContent($"{zipHash}  SweetHide-1.0.0-win-x64.zip\n"),
                _ when url.EndsWith("x64.zip", StringComparison.Ordinal) => new ByteArrayContent(ZipBytes),
                _ => throw new InvalidOperationException(url),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host");
    }
}
