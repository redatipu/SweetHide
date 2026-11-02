using SweetHide.Core;
using SweetHide.Core.Payloads;
using SweetHide.Presentation.Imaging;

namespace SweetHide.Presentation.Services;

/// <param name="Image">The decoded image.</param>
/// <param name="Payload">What was hidden in it.</param>
/// <param name="VaultName">Name of the vault entry whose password worked.</param>
public sealed record AutoRevealResult(LoadedImage Image, Payload Payload, string VaultName);

/// <summary>Tries every remembered password on an image, newest first. Used by Reveal and the watched folder.</summary>
public sealed class AutoRevealer(StegoEngine engine, ImageLoader loader, IPasswordVault vault)
{
    public IPasswordVault Vault { get; } = vault;

    public async Task<AutoRevealResult?> TryFileAsync(string path, IProgress<(int Tried, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var image = await loader.LoadFileAsync(path, ImagePurpose.Inspect);
        return await TryImageAsync(image, progress, cancellationToken);
    }

    public async Task<AutoRevealResult?> TryImageAsync(LoadedImage image, IProgress<(int Tried, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var entries = Vault.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((i, entries.Count));
            var entry = entries[i];
            try
            {
                var payload = await Task.Run(() => engine.Extract(image.Pixels, entry.Password), cancellationToken);
                Vault.Touch(entry.Name);
                return new AutoRevealResult(image, payload, entry.Name);
            }
            catch (StegoException ex) when (ex.Error == StegoError.NotFoundOrWrongPassword)
            {
                // Not this one; keep trying.
            }
        }

        progress?.Report((entries.Count, entries.Count));
        return null;
    }
}

/// <summary>
/// Watches a folder (e.g. Downloads) for new PNG files and hands each one over once it has finished writing.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, Task> _onImage;
    private readonly TimeSpan _settle;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public FolderWatcher(string folder, Func<string, Task> onImage, TimeSpan? settle = null)
    {
        _onImage = onImage;
        _settle = settle ?? TimeSpan.FromSeconds(1);
        _watcher = new FileSystemWatcher(folder)
        {
            Filter = "*.png",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, e) => Schedule(e.FullPath);
        _watcher.Renamed += (_, e) => Schedule(e.FullPath); // browsers download to a temp name, then rename
        _watcher.EnableRaisingEvents = true;
    }

    public string Folder => _watcher.Path;

    public void Dispose() => _watcher.Dispose();

    private void Schedule(string path)
    {
        lock (_pending)
        {
            if (!_pending.Add(path))
            {
                return;
            }
        }

        _ = HandleAsync(path);
    }

    private async Task HandleAsync(string path)
    {
        try
        {
            // Wait until the size stops changing, so we never read a half-written download.
            long lastSize = -1;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(_settle);
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return;
                }

                if (info.Length == lastSize && info.Length > 0)
                {
                    await _onImage(path);
                    return;
                }

                lastSize = info.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Imaging.InvalidImageException)
        {
            // Not readable or not an image we can open: ignore.
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(path);
            }
        }
    }
}
