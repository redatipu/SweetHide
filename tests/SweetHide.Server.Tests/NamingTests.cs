namespace SweetHide.Server.Tests;

public sealed class NamingTests
{
    [Theory]
    [InlineData("beach.jpg", ".png", "beach.png")]
    [InlineData("ทะเล.webp", ".png", "ทะเล.png")]
    [InlineData("../../etc/passwd", ".png", "passwd.png")]
    [InlineData("C:\\Users\\me\\photo.JPG", ".zip", "photo.zip")]
    [InlineData("CON.png", ".png", "_CON.png")]
    [InlineData(".png", ".png", "image.png")]
    [InlineData("", ".png", "image.png")]
    [InlineData(null, ".zip", "image.zip")]
    public void Download_names_follow_the_upload_without_hinting_at_the_contents(string? uploaded, string extension, string expected) =>
        Assert.Equal(expected, StegoApi.DownloadName(uploaded, extension));

    [Fact]
    public void Duplicate_zip_entries_get_numbered_case_insensitively()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("a.txt", StegoApi.UniqueName("a.txt", used));
        Assert.Equal("A (1).TXT", StegoApi.UniqueName("A.TXT", used));
        Assert.Equal("a (2).txt", StegoApi.UniqueName("a.txt", used));
        Assert.Equal("README", StegoApi.UniqueName("README", used));
        Assert.Equal("README (1)", StegoApi.UniqueName("README", used));
    }

    [Fact]
    public void Health_probe_targets_the_configured_port() =>
        Assert.StartsWith("http://127.0.0.1:", HealthProbe.DefaultUrl(), StringComparison.Ordinal);
}
