using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SweetHide.Presentation.Services;

/// <param name="Version">Version of the newer release, without the leading "v".</param>
/// <param name="PageUrl">The release page, for reading the notes in a browser.</param>
/// <param name="AssetName">Download for this machine (e.g. SweetHide-1.2.0-win-x64.zip), if the release has one.</param>
/// <param name="AssetUrl">Where to download <paramref name="AssetName"/>.</param>
/// <param name="ChecksumsUrl">SHA256SUMS.txt of the release.</param>
public sealed record UpdateInfo(string Version, Uri PageUrl, string? AssetName, Uri? AssetUrl, Uri? ChecksumsUrl);

public interface IUpdateService
{
    /// <summary>The newest release if it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>Downloads the asset into <paramref name="folder"/> and verifies it against SHA256SUMS.txt.</summary>
    /// <returns>The verified file path.</returns>
    /// <exception cref="InvalidDataException">The checksum is missing or does not match (the file is deleted).</exception>
    Task<string> DownloadAsync(UpdateInfo update, string folder, CancellationToken cancellationToken = default);
}

/// <summary>Uses the public GitHub Releases API. Nothing is sent except the request itself.</summary>
public sealed class GitHubUpdateService(HttpClient http, string repository = "redatipu/SweetHide", string assetSuffix = "-win-x64.zip") : IUpdateService
{
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{repository}/releases/latest"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no releases yet
        }

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync(UpdateJsonContext.Default.GitHubRelease, cancellationToken)
            ?? throw new InvalidDataException("Empty release information.");
        var latest = release.TagName.TrimStart('v', 'V');
        if (release.Draft || release.Prerelease || SemanticVersion.Compare(latest, currentVersion) <= 0)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a => a.Name.StartsWith("SweetHide-", StringComparison.Ordinal) && a.Name.EndsWith(assetSuffix, StringComparison.Ordinal));
        var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt");
        return new UpdateInfo(latest, release.HtmlUrl, asset?.Name, asset?.DownloadUrl, sums?.DownloadUrl);
    }

    public async Task<string> DownloadAsync(UpdateInfo update, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.AssetUrl is null || update.AssetName is null || update.ChecksumsUrl is null)
        {
            throw new InvalidDataException("This release has no download for this computer.");
        }

        string sums;
        using (var sumsResponse = await GetAsync(update.ChecksumsUrl, cancellationToken))
        {
            sums = await sumsResponse.EnsureSuccessStatusCode().Content.ReadAsStringAsync(cancellationToken);
        }

        var expected = Checksums.Find(sums, update.AssetName)
            ?? throw new InvalidDataException($"{update.AssetName} is not listed in SHA256SUMS.txt.");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Core.SafeFileName.Sanitize(update.AssetName));
        using (var response = await GetAsync(update.AssetUrl, cancellationToken))
        await using (var file = File.Create(path))
        {
            await response.EnsureSuccessStatusCode().Content.CopyToAsync(file, cancellationToken);
        }

        string actual;
        await using (var file = File.OpenRead(path))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException($"Checksum mismatch for {update.AssetName}; the download was deleted.");
        }

        return path;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SweetHide-UpdateCheck"); // GitHub's API rejects requests without one
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Accept.ParseAdd("application/octet-stream");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}

/// <summary>Reads lines of "&lt;sha256&gt;  &lt;file name&gt;" (sha256sum format).</summary>
public static class Checksums
{
    public static string? Find(string sha256Sums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(sha256Sums);
        foreach (var line in sha256Sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space == 64 && line[(space + 1)..].TrimStart(' ', '*') == fileName)
            {
                return line[..64];
            }
        }

        return null;
    }
}

/// <summary>Just enough SemVer 2.0 precedence for comparing release tags.</summary>
public static class SemanticVersion
{
    public static int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        for (var i = 0; i < 3; i++)
        {
            var c = leftCore[i].CompareTo(rightCore[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // A version without a pre-release tag ranks higher than one with it.
        if (leftPre is null || rightPre is null)
        {
            return (leftPre is null ? 1 : 0) - (rightPre is null ? 1 : 0);
        }

        var leftParts = leftPre.Split('.');
        var rightParts = rightPre.Split('.');
        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var leftNumeric = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ln);
            var rightNumeric = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rn);
            var c = (leftNumeric, rightNumeric) switch
            {
                (true, true) => ln.CompareTo(rn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[i], rightParts[i]),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static (int[] Core, string? PreRelease) Split(string version)
    {
        var withoutBuild = version.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? withoutBuild : withoutBuild[..dash];
        var parts = core.Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            numbers[i] = i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        return (numbers, dash < 0 ? null : withoutBuild[(dash + 1)..]);
    }
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] Uri HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] Uri DownloadUrl);

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
