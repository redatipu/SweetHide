using System.Security.Cryptography;

using SweetHide.Core.Crypto;

namespace SweetHide.Core.Stego;

/// <summary>A password that opened something: a v2 container or a v1 image.</summary>
internal sealed class PasswordMatch : IDisposable
{
    public PasswordMatch(OpenedContainer v2) => V2 = v2;

    public PasswordMatch(OpenedV1 v1) => V1 = v1;

    public OpenedContainer? V2 { get; }

    public OpenedV1? V1 { get; }

    public void Dispose()
    {
        V2?.Dispose();
        V1?.Dispose();
    }
}

/// <summary>
/// Everything that can be learned from an image without a key, worked out once: the slot geometry of both formats,
/// the salt and the KDF profile it selects. Then each password costs one Argon2id run (two when the image's profile
/// differs from the v1 parameters and v2 did not match).
/// </summary>
/// <remarks>Read-only and thread-safe, so a password probe can test candidates in parallel.</remarks>
internal sealed class StegoImageReader
{
    private readonly KdfProfiles _profiles;
    private readonly byte[]? _salt;

    public StegoImageReader(IStegoCarrier carrier, KdfProfiles profiles)
    {
        Carrier = carrier;
        _profiles = profiles;
        V2 = new V2Layout(carrier);
        V1 = carrier is RgbLsbCarrier ? new SlotLayout(carrier) : null;

        if (V2.HasPublicRegion)
        {
            PublicBytes = new byte[V2Layout.PublicBytes];
            V2.ReadRanks(0, PublicBytes);
            _salt = PublicBytes[..ProfileCode.SaltSize];
        }
        else if (V1?.HasRoom == true)
        {
            // Same slots as the first 16 public bytes; read separately only because v2 has no room here.
            _salt = V1Format.ReadSalt(carrier, V1);
        }

        Profile = _salt is null ? KdfProfile.Normal : ProfileCode.FromSalt(_salt);
    }

    public IStegoCarrier Carrier { get; }

    public V2Layout V2 { get; }

    public SlotLayout? V1 { get; }

    /// <summary>The 32 public bytes (salt and filler, or an ephemeral public key), or null if the image is too small.</summary>
    public byte[]? PublicBytes { get; }

    /// <summary>The profile the salt selects (meaningful only if the image holds v2 password-mode data).</summary>
    public KdfProfile Profile { get; }

    public bool CanHoldPasswordData => _salt is not null;

    /// <returns>What the password opened, or null.</returns>
    /// <exception cref="StegoException">The password is right but the data is damaged or from a newer version.</exception>
    public PasswordMatch? TryPassword(ReadOnlySpan<char> password, KeyFile? keyFile)
    {
        if (_salt is null)
        {
            return null;
        }

        var v2Parameters = _profiles[Profile];
        byte[]? master = null;
        byte[]? v1Master = null;
        try
        {
            if (V2.HasPublicRegion)
            {
                master = KeyDerivation.Argon2Master(password, _salt, v2Parameters);
                var root = KeyDerivation.PasswordRoot(master, keyFile);
                try
                {
                    var opened = V2Container.TryOpen(V2, V2Layout.PublicSlots, root);
                    if (opened is not null)
                    {
                        return new PasswordMatch(opened);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(root);
                }
            }

            // v1 had no key files, and always used the Normal parameters.
            if (keyFile is null && V1?.HasRoom == true)
            {
                v1Master = master is not null && v2Parameters == _profiles.Normal
                    ? master
                    : KeyDerivation.Argon2Master(password, _salt, _profiles.Normal);
                var opened = V1Format.TryOpen(Carrier, V1, v1Master);
                if (opened is not null)
                {
                    return new PasswordMatch(opened);
                }
            }

            return null;
        }
        finally
        {
            if (master is not null)
            {
                CryptographicOperations.ZeroMemory(master);
            }

            if (v1Master is not null)
            {
                CryptographicOperations.ZeroMemory(v1Master);
            }
        }
    }
}
