using System.Text.Json;
using System.Text.Json.Serialization;

namespace SweetHide.Presentation.Services;

/// <param name="Name">Who or what the password is for (e.g. a friend's name).</param>
/// <param name="Password">The password itself.</param>
/// <param name="LastUsed">Used to try the most recent ones first.</param>
public sealed record VaultEntry(string Name, string Password, DateTimeOffset LastUsed);

/// <summary>Passwords remembered per friend, so revealing can try them automatically.</summary>
public interface IPasswordVault
{
    IReadOnlyList<VaultEntry> Entries { get; }

    /// <summary>Adds or replaces the entry with this name (case-insensitive).</summary>
    void Save(string name, string password);

    void Remove(string name);

    /// <summary>Marks an entry as just used so it is tried first next time.</summary>
    void Touch(string name);
}

/// <summary>Encrypts data at rest for the current user (DPAPI on Windows).</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>A JSON list of entries, encrypted as a whole with <see cref="IDataProtector"/>.</summary>
public sealed class ProtectedFileVault : IPasswordVault
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly List<VaultEntry> _entries;
    private readonly TimeProvider _time;

    public ProtectedFileVault(string path, IDataProtector protector, TimeProvider? time = null)
    {
        _path = path;
        _protector = protector;
        _time = time ?? TimeProvider.System;
        _entries = Load();
    }

    /// <summary>Raised after any change, so lists in the UI can refresh.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<VaultEntry> Entries => [.. _entries.OrderByDescending(e => e.LastUsed)];

    public void Save(string name, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(password);
        _entries.RemoveAll(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        _entries.Add(new VaultEntry(name.Trim(), password, _time.GetUtcNow()));
        Persist();
    }

    public void Remove(string name)
    {
        if (_entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Persist();
        }
    }

    public void Touch(string name)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { LastUsed = _time.GetUtcNow() };
            Persist();
        }
    }

    private List<VaultEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var json = _protector.Unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize(json, VaultJsonContext.Default.ListVaultEntry) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Unreadable (another user/PC, or damaged): start empty rather than crash. The old file is kept aside.
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }

            return [];
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var json = JsonSerializer.SerializeToUtf8Bytes(_entries, VaultJsonContext.Default.ListVaultEntry);
        try
        {
            File.WriteAllBytes(_path, _protector.Protect(json));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(json);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

[JsonSerializable(typeof(List<VaultEntry>))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;
