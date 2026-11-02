using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using SweetHide.Core.Crypto;

namespace SweetHide.Presentation.Services;

/// <param name="Name">What the user calls this friend.</param>
/// <param name="PublicKey">Their <c>sweethide1…</c> key, already validated.</param>
public sealed record FriendKey(string Name, string PublicKey);

/// <summary>"My key" (an X25519 identity) and the public keys of friends, for public-key hiding.</summary>
public interface IKeyring
{
    /// <summary>Raised after any change, so pages can refresh.</summary>
    event EventHandler? Changed;

    bool HasIdentity { get; }

    /// <summary>My public key (<c>sweethide1…</c>) to give to friends, or null when there is no key yet.</summary>
    string? PublicKey { get; }

    /// <summary>Friends sorted by name.</summary>
    IReadOnlyList<FriendKey> Friends { get; }

    /// <summary>A fresh copy of my private key for one operation (dispose it after use), or null.</summary>
    Identity? OpenIdentity();

    /// <summary>Creates a new key, replacing the current one.</summary>
    /// <returns>The new public key.</returns>
    string GenerateIdentity();

    /// <summary>Adds or replaces the friend with this name (case-insensitive).</summary>
    /// <exception cref="FormatException">The key is not a valid SweetHide public key.</exception>
    void SaveFriend(string name, string publicKey);

    void RemoveFriend(string name);
}

/// <summary>
/// The keyring as one JSON document encrypted as a whole with <see cref="IDataProtector"/> (DPAPI on Windows), like
/// the password vault.
/// </summary>
/// <remarks>
/// <see cref="Identity"/> only leaves memory through its passphrase-protected export, so the private key is stored in
/// that form, with a random passphrase kept next to it. DPAPI is what actually protects both; the export passphrase
/// therefore uses a cheap Argon2id setting so opening the key stays instant.
/// </remarks>
public sealed class ProtectedKeyring : IKeyring
{
    private static readonly KdfParameters StorageKdf = new(1024, 1, 1);

    private readonly string _path;
    private readonly IDataProtector _protector;
    private KeyringData _data;

    public ProtectedKeyring(string path, IDataProtector protector)
    {
        _path = path;
        _protector = protector;
        _data = Load();
    }

    public event EventHandler? Changed;

    public bool HasIdentity => _data.Identity is not null;

    public string? PublicKey => _data.Identity?.PublicKey;

    public IReadOnlyList<FriendKey> Friends =>
        [.. _data.Friends.Select(f => new FriendKey(f.Name, f.PublicKey)).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];

    public Identity? OpenIdentity()
    {
        if (_data.Identity is not { } stored)
        {
            return null;
        }

        try
        {
            return Identity.Import(stored.Export, stored.Passphrase);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null; // damaged; Settings offers to make a new one
        }
    }

    public string GenerateIdentity()
    {
        using var identity = Identity.Generate();
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var publicKey = identity.Recipient.ToString();
        _data = _data with { Identity = new StoredIdentity(publicKey, identity.Export(passphrase, StorageKdf), passphrase) };
        Persist();
        return publicKey;
    }

    public void SaveFriend(string name, string publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var recipient = Recipient.Parse(publicKey); // throws FormatException for anything mistyped
        var friends = _data.Friends.Where(f => !string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        friends.Add(new StoredFriend(name.Trim(), recipient.ToString()));
        _data = _data with { Friends = friends };
        Persist();
    }

    public void RemoveFriend(string name)
    {
        var friends = _data.Friends.Where(f => !string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (friends.Count != _data.Friends.Count)
        {
            _data = _data with { Friends = friends };
            Persist();
        }
    }

    private KeyringData Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new KeyringData();
            }

            var json = _protector.Unprotect(File.ReadAllBytes(_path));
            var data = JsonSerializer.Deserialize(json, KeyringJsonContext.Default.KeyringData) ?? new KeyringData();
            return data with { Friends = [.. data.Friends.Where(f => Recipient.TryParse(f.PublicKey, out _))] };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Unreadable (another user or PC, or damaged): start empty, but keep the old file aside.
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }

            return new KeyringData();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var json = JsonSerializer.SerializeToUtf8Bytes(_data, KeyringJsonContext.Default.KeyringData);
        try
        {
            File.WriteAllBytes(_path, _protector.Protect(json));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record StoredIdentity(string PublicKey, string Export, string Passphrase);

internal sealed record StoredFriend(string Name, string PublicKey);

internal sealed record KeyringData
{
    public StoredIdentity? Identity { get; init; }

    public IReadOnlyList<StoredFriend> Friends { get; init; } = [];
}

[JsonSerializable(typeof(KeyringData))]
internal sealed partial class KeyringJsonContext : JsonSerializerContext;
