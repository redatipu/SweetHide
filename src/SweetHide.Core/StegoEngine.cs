using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core;

/// <summary>
/// Hides an encrypted <see cref="Payload"/> in the low bits of an image and gets it back. See docs/FORMAT.md.
/// </summary>
/// <remarks>
/// New images are written in format v2; images made by v0.1.0 (format v1) still reveal. Layout of v2:
/// <list type="number">
/// <item>32 public bytes (a random salt, or an ephemeral public key) at 1 bit per slot in fixed pseudo-random slots.
/// The salt also selects the Argon2id profile, so nothing about the settings is written in clear.</item>
/// <item>Argon2id(password, salt), mixed with an optional key file → a root key → an AES-256-GCM key and the key of
/// the container's slot permutation.</item>
/// <item>Along that permutation: an encrypted 27-byte header (bits per channel, adaptive flag, lengths) at 1 bit per
/// slot, then the encrypted body. No plaintext marker anywhere.</item>
/// </list>
/// </remarks>
public sealed class StegoEngine
{
    public const int DefaultBitsPerChannel = 1;

    /// <summary>Header (11 bytes + tag) plus body tag.</summary>
    public const int ContainerOverhead = V2Container.Overhead;

    /// <summary>Most recipients one image can be hidden for.</summary>
    public const int MaxRecipients = RecipientStanza.MaxRecipients;

    private readonly KdfProfiles _profiles;
    private readonly IRandomSource _random;

    /// <param name="kdfParameters">
    /// Null or <see cref="KdfParameters.Default"/> selects the standard profiles (<see cref="KdfProfiles.Default"/>).
    /// Any other value pins every profile, and v1 reading, to those parameters (tests and custom setups).
    /// </param>
    public StegoEngine(KdfParameters? kdfParameters = null)
        : this(kdfParameters is null || kdfParameters == KdfParameters.Default ? KdfProfiles.Default : KdfProfiles.Uniform(kdfParameters))
    {
    }

    /// <param name="profiles">Argon2id parameters per profile. <see cref="KdfProfiles.Normal"/> is also used for v1 images.</param>
    public StegoEngine(KdfProfiles profiles)
        : this(profiles, SystemRandomSource.Instance)
    {
    }

    internal StegoEngine(KdfProfiles profiles, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        profiles.Validate();
        _profiles = profiles;
        _random = random;
    }

    /// <summary>Bits per channel values the engine can write.</summary>
    public static IReadOnlyList<int> SupportedBitsPerChannel { get; } = [1, 2, 3, 4];

    /// <summary>Capacity of a normal password-mode embed.</summary>
    public static CapacityInfo GetCapacity(PixelBuffer image, int bitsPerChannel = DefaultBitsPerChannel)
    {
        ValidateBits(bitsPerChannel);
        return GetCapacity(image, EmbedOptions.Default with { BitsPerChannel = bitsPerChannel });
    }

    /// <summary>Capacity of a password-mode embed with these options (adaptive: with every textured slot allowed).</summary>
    public static CapacityInfo GetCapacity(PixelBuffer image, EmbedOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var carrier = new RgbLsbCarrier(image);
        var layout = new V2Layout(carrier);
        return Capacity(image, carrier, ContainerBytes(layout, V2Layout.PublicSlots, ContainerPlacement.Whole, options), options);
    }

    /// <summary>
    /// The smallest bits per channel at which <paramref name="payload"/> fits a password-mode embed with
    /// <paramref name="options"/> (its own bits value is ignored), or null if it does not fit even at 4.
    /// Fewer bits change the image less, so this is also the least detectable choice.
    /// </summary>
    public static int? RecommendBitsPerChannel(PixelBuffer cover, Payload payload, EmbedOptions? options = null) =>
        RecommendBitsPerChannel(cover, GetContainerSize(payload), options);

    /// <inheritdoc cref="RecommendBitsPerChannel(PixelBuffer, Payload, EmbedOptions?)"/>
    /// <param name="cover">The cover image.</param>
    /// <param name="containerSize">From <see cref="GetContainerSize"/> or <see cref="GetShareContainerSize"/>.</param>
    /// <param name="options">Adaptive or not; the bits value is ignored.</param>
    public static int? RecommendBitsPerChannel(PixelBuffer cover, long containerSize, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        options ??= EmbedOptions.Default;
        options.Validate();
        var layout = new V2Layout(new RgbLsbCarrier(cover));
        foreach (var bits in SupportedBitsPerChannel)
        {
            if (ContainerBytes(layout, V2Layout.PublicSlots, ContainerPlacement.Whole, options with { BitsPerChannel = bits }) >= containerSize)
            {
                return bits;
            }
        }

        return null;
    }

    /// <summary>Bytes the payload will occupy in the image (after compression, with encryption overhead).</summary>
    public static long GetContainerSize(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ContainerOverhead + PayloadSerializer.Serialize(payload).LongLength;
    }

    /// <summary>Returns a new image carrying <paramref name="payload"/>. The cover is not modified.</summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer Embed(PixelBuffer cover, Payload payload, ReadOnlySpan<char> password, int bitsPerChannel = DefaultBitsPerChannel)
    {
        ValidateBits(bitsPerChannel);
        return Embed(cover, payload, password, keyFile: null, EmbedOptions.Default with { BitsPerChannel = bitsPerChannel });
    }

    /// <summary>Password mode, optionally with a key file as a second factor.</summary>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer Embed(PixelBuffer cover, Payload payload, ReadOnlySpan<char> password, KeyFile? keyFile, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(payload);
        options ??= EmbedOptions.Default;
        options.Validate();
        RequirePassword(password);

        var content = PayloadSerializer.Serialize(payload);
        try
        {
            return EmbedPassword(cover, content, ContentKind.Payload, password, keyFile, options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>Capacity of <em>each</em> part of a deniable image (each gets half of the data region).</summary>
    public static CapacityInfo GetDeniableCapacity(PixelBuffer image, int bitsPerChannel = DefaultBitsPerChannel)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateBits(bitsPerChannel);
        var options = EmbedOptions.Default with { BitsPerChannel = bitsPerChannel };
        var carrier = new RgbLsbCarrier(image);
        var layout = new V2Layout(carrier);
        return Capacity(image, carrier, ContainerBytes(layout, V2Layout.PublicSlots, ContainerPlacement.FirstHalf, options), options);
    }

    /// <summary>
    /// Deniable mode: two payloads, each revealed only by its own password, and neither password shows whether the
    /// other payload exists.
    /// </summary>
    /// <remarks>
    /// The data region is split into two halves and each part goes into a random half, padded until it fills that
    /// half completely. Without <paramref name="second"/> the other half is filled with a container under a random
    /// key, which looks exactly like a second payload; that is what makes "there is nothing else" believable.
    /// The price: each payload gets at most half the capacity (see <see cref="GetDeniableCapacity"/>), and the whole
    /// image is always used, so it is easier to tell that <em>something</em> is hidden than with a small normal embed.
    /// Both parts share the salt, and therefore the KDF profile.
    /// </remarks>
    /// <exception cref="ArgumentException">Both parts would have the same key, or adaptive embedding was requested.</exception>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer EmbedDeniable(PixelBuffer cover, DeniablePart first, DeniablePart? second, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(first);
        options ??= EmbedOptions.Default;
        options.Validate();
        if (options.Adaptive)
        {
            throw new ArgumentException("Deniable mode fills the whole image, so adaptive embedding does not apply.", nameof(options));
        }

        RequirePassword(first.Password);
        if (second is not null)
        {
            RequirePassword(second.Password);
        }

        var contents = new List<byte[]>(2);
        var roots = new List<byte[]>(2);
        try
        {
            contents.Add(PayloadSerializer.Serialize(first.Payload));
            contents.Add(second is null ? [] : PayloadSerializer.Serialize(second.Payload));

            var stego = cover.Clone();
            var layout = new V2Layout(new RgbLsbCarrier(stego));
            var capacity = ContainerBytes(layout, V2Layout.PublicSlots, ContainerPlacement.FirstHalf, options); // the smaller half
            contents.ForEach(content => RequireCapacity(capacity, content.Length, options));

            var publicBytes = NewPasswordPublicBytes(options.KdfProfile);
            var salt = publicBytes.AsSpan(0, ProfileCode.SaltSize);
            roots.Add(PasswordRoot(first.Password, first.KeyFile, salt, options.KdfProfile));
            roots.Add(second is null ? RandomBytes(KeyDerivation.RootKeySize) : PasswordRoot(second.Password, second.KeyFile, salt, options.KdfProfile));
            if (CryptographicOperations.FixedTimeEquals(roots[0], roots[1]))
            {
                throw new ArgumentException("The two parts need different passwords (or different key files).", nameof(second));
            }

            // Which half gets which part is random, so neither half means "decoy".
            var firstHalf = (RandomBytes(1)[0] & 1) == 0 ? 0 : 1;
            layout.WriteRanks(0, publicBytes, new RandomBits(_random));
            for (var part = 0; part < 2; part++)
            {
                var placement = (part ^ firstHalf) == 0 ? ContainerPlacement.FirstHalf : ContainerPlacement.SecondHalf;
                V2Container.Write(
                    layout, V2Layout.PublicSlots, placement, roots[part], ContentKind.Payload, contents[part], options.BitsPerChannel, adaptive: false, fill: true, _random);
            }

            return stego;
        }
        finally
        {
            contents.ForEach(bytes => CryptographicOperations.ZeroMemory(bytes));
            roots.ForEach(bytes => CryptographicOperations.ZeroMemory(bytes));
        }
    }

    /// <summary>Capacity of a public-key embed for <paramref name="recipientCount"/> recipients.</summary>
    public static CapacityInfo GetRecipientCapacity(PixelBuffer image, int recipientCount, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(recipientCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recipientCount, MaxRecipients);
        options ??= EmbedOptions.Default;
        options.Validate();
        var carrier = new RgbLsbCarrier(image);
        var layout = new V2Layout(carrier);
        return Capacity(image, carrier, ContainerBytes(layout, RecipientDataStart(recipientCount), ContainerPlacement.Whole, options), options);
    }

    /// <summary>
    /// Public-key mode: anyone holding the <see cref="Identity"/> of one of <paramref name="recipients"/> can reveal it,
    /// no password needed. Recipients are not listed anywhere in the image.
    /// </summary>
    /// <remarks>
    /// The image stores one ephemeral X25519 public key. Its top bit is randomised (X25519 ignores it), but a curve
    /// point is still not uniformly random: about half of all 255-bit strings are valid points, so an observer who
    /// suspects this mode gains at most one bit of evidence. See docs/FORMAT.md.
    /// </remarks>
    /// <exception cref="ArgumentException">No recipients, or more than <see cref="MaxRecipients"/>.</exception>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/>.</exception>
    public PixelBuffer Embed(PixelBuffer cover, Payload payload, IReadOnlyCollection<Recipient> recipients, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(recipients);
        options ??= EmbedOptions.Default;
        options.Validate();
        var distinct = recipients.Select(r => r ?? throw new ArgumentException("Recipients must not be null.", nameof(recipients))).Distinct().ToList();
        if (distinct.Count is 0 or > MaxRecipients)
        {
            throw new ArgumentException($"Hide for 1 to {MaxRecipients} recipients.", nameof(recipients));
        }

        var content = PayloadSerializer.Serialize(payload);
        var fileKey = RandomBytes(RecipientStanza.FileKeySize);
        var ephemeralSecret = RandomBytes(32);
        try
        {
            var stego = cover.Clone();
            var layout = new V2Layout(new RgbLsbCarrier(stego));
            var dataStart = RecipientDataStart(distinct.Count);
            RequireCapacity(ContainerBytes(layout, dataStart, ContainerPlacement.Whole, options), content.Length, options);

            // Table order is random too, so an entry's position says nothing about who was listed first.
            for (var i = distinct.Count - 1; i > 0; i--)
            {
                var j = (int)(BitConverter.ToUInt32(RandomBytes(4)) % (uint)(i + 1));
                (distinct[i], distinct[j]) = (distinct[j], distinct[i]);
            }

            using var ephemeral = NSec.Cryptography.Key.Import(
                NSec.Cryptography.KeyAgreementAlgorithm.X25519, ephemeralSecret, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
            var ephemeralPublic = ephemeral.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
            var stanzas = distinct.Select(r => RecipientStanza.Wrap(ephemeral, ephemeralPublic, r, fileKey, distinct.Count)).ToList();

            var publicBytes = (byte[])ephemeralPublic.Clone();
            publicBytes[^1] |= (byte)(RandomBytes(1)[0] & 0x80);
            var randomBits = new RandomBits(_random);
            layout.WriteRanks(0, publicBytes, randomBits);
            for (var i = 0; i < stanzas.Count; i++)
            {
                layout.WriteRanks(V2Layout.PublicSlots + ((long)i * RecipientStanza.Slots), stanzas[i], randomBits);
            }

            V2Container.Write(
                layout, dataStart, ContainerPlacement.Whole, fileKey, ContentKind.Payload, content, options.BitsPerChannel, options.Adaptive, fill: false, _random);
            return stego;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            CryptographicOperations.ZeroMemory(fileKey);
            CryptographicOperations.ZeroMemory(ephemeralSecret);
        }
    }

    /// <summary>Reveals data hidden for <paramref name="identity"/>'s public key.</summary>
    /// <inheritdoc cref="Extract(PixelBuffer, ReadOnlySpan{char})"/>
    public Payload Extract(PixelBuffer image, Identity identity) => PayloadOf(Reveal(image, identity));

    /// <inheritdoc cref="Extract(PixelBuffer, Identity)"/>
    public RevealResult Reveal(PixelBuffer image, Identity identity)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(identity);
        var reader = new StegoImageReader(new RgbLsbCarrier(image), _profiles);
        using var container = reader.TryIdentity(identity) ?? throw NotFound();
        return FromContainer(reader.Carrier, container, HidingMode.Recipients, profile: null);
    }

    /// <summary>A <see cref="PasswordProbe"/> for auditing the password of one of your own images.</summary>
    /// <param name="image">The stego image; it is copied, so later changes to it do not matter.</param>
    public PasswordProbe CreatePasswordProbe(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PasswordProbe(new StegoImageReader(new RgbLsbCarrier(image.Clone()), _profiles));
    }

    /// <summary>Bytes each image of a split must hold (see <see cref="EmbedShares"/>).</summary>
    public static long GetShareContainerSize(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ContainerOverhead + ShamirShare.EncodedSize(PayloadSerializer.Serialize(payload).LongLength);
    }

    /// <summary>
    /// Splits a payload across <paramref name="covers"/> (one share each, Shamir over GF(256)) so that any
    /// <paramref name="threshold"/> of the images, with the password, rebuild it and fewer reveal nothing about it.
    /// </summary>
    /// <remarks>
    /// Every image holds a full-size share (<see cref="GetShareContainerSize"/>) under its own salt. Revealing one
    /// image gives a <see cref="ShamirShare"/> (its number, the threshold and the payload's size, but none of its
    /// content); <see cref="CombineShares"/> rebuilds the payload.
    /// </remarks>
    /// <param name="covers">One cover per share; 2 to 255 of them.</param>
    /// <param name="payload">What to split.</param>
    /// <param name="threshold">Shares needed to rebuild, 2 to the number of covers.</param>
    /// <param name="password">Protects every image.</param>
    /// <param name="keyFile">Optional second factor for every image.</param>
    /// <param name="options">Embedding options for every image.</param>
    /// <exception cref="StegoException"><see cref="StegoError.InsufficientCapacity"/> if any cover is too small.</exception>
    public IReadOnlyList<PixelBuffer> EmbedShares(
        IReadOnlyList<PixelBuffer> covers, Payload payload, int threshold, ReadOnlySpan<char> password, KeyFile? keyFile = null, EmbedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(payload);
        if (covers.Count is < 2 or > 255 || covers.Any(c => c is null))
        {
            throw new ArgumentException("Split across 2 to 255 cover images.", nameof(covers));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, covers.Count);
        options ??= EmbedOptions.Default;
        options.Validate();
        RequirePassword(password);

        var payloadBytes = PayloadSerializer.Serialize(payload);
        var shareSize = ShamirShare.EncodedSize(payloadBytes.LongLength);
        foreach (var cover in covers)
        {
            RequireCapacity(ContainerBytes(new V2Layout(new RgbLsbCarrier(cover)), V2Layout.PublicSlots, ContainerPlacement.Whole, options), shareSize, options);
        }

        var shares = ShamirShare.Split(payloadBytes, threshold, covers.Count, _random);
        try
        {
            var images = new PixelBuffer[covers.Count];
            for (var i = 0; i < covers.Count; i++)
            {
                images[i] = EmbedPassword(covers[i], shares[i], ContentKind.Share, password, keyFile, options);
            }

            return images;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            Array.ForEach(shares, share => CryptographicOperations.ZeroMemory(share));
        }
    }

    /// <summary>Rebuilds a split payload from the shares revealed from its images (at least the threshold).</summary>
    /// <exception cref="StegoException">
    /// <see cref="StegoError.NotEnoughShares"/>, <see cref="StegoError.SharesDoNotMatch"/> or
    /// <see cref="StegoError.Corrupted"/> (a share was altered).
    /// </exception>
    public static Payload CombineShares(IEnumerable<ShamirShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return ShamirShare.Combine(shares);
    }

    /// <exception cref="StegoException">
    /// <see cref="StegoError.NotFoundOrWrongPassword"/>, <see cref="StegoError.Corrupted"/>,
    /// <see cref="StegoError.UnsupportedFormat"/> or <see cref="StegoError.SplitShare"/> (use <see cref="Reveal(PixelBuffer, ReadOnlySpan{char}, KeyFile?)"/>).
    /// </exception>
    public Payload Extract(PixelBuffer image, ReadOnlySpan<char> password) => Extract(image, password, keyFile: null);

    /// <inheritdoc cref="Extract(PixelBuffer, ReadOnlySpan{char})"/>
    public Payload Extract(PixelBuffer image, ReadOnlySpan<char> password, KeyFile? keyFile) =>
        PayloadOf(Reveal(image, password, keyFile));

    /// <summary>Like <see cref="Extract(PixelBuffer, ReadOnlySpan{char}, KeyFile?)"/>, and also says how the data was hidden.</summary>
    /// <inheritdoc cref="Extract(PixelBuffer, ReadOnlySpan{char})"/>
    public RevealResult Reveal(PixelBuffer image, ReadOnlySpan<char> password, KeyFile? keyFile = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequirePassword(password);

        var reader = new StegoImageReader(new RgbLsbCarrier(image), _profiles);
        using var match = reader.TryPassword(password, keyFile) ?? throw NotFound();
        if (match.V1 is { } v1)
        {
            var body = V1Format.ReadBody(v1);
            try
            {
                return new RevealResult(new HiddenDataInfo(1, HidingMode.Password, v1.BitsPerSlot, false, KdfProfile.Normal), Deserialize(body), null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }

        var mode = match.V2!.Placement == ContainerPlacement.Whole ? HidingMode.Password : HidingMode.Deniable;
        return FromContainer(reader.Carrier, match.V2, mode, reader.Profile);
    }

    internal static long ContainerBytes(V2Layout layout, long dataStart, ContainerPlacement placement, EmbedOptions options)
    {
        if (!layout.HasPublicRegion)
        {
            return 0;
        }

        var bodySlots = options.Adaptive
            ? TexturedDataSlots(layout, dataStart) - ContainerSlots.HeaderSlotCount
            : layout.PlacementLength(dataStart, placement) - ContainerSlots.HeaderSlotCount;
        return V2Container.ContainerBytes(bodySlots, options.BitsPerChannel);
    }

    private static long RecipientDataStart(int recipientCount) => V2Layout.PublicSlots + ((long)recipientCount * RecipientStanza.Slots);

    private static long TexturedDataSlots(V2Layout layout, long dataStart)
    {
        if (layout.Carrier is not IAdaptiveCarrier carrier)
        {
            return 0;
        }

        var textured = carrier.TexturedSlotCount;
        for (long rank = 0; rank < Math.Min(dataStart, layout.SlotCount); rank++)
        {
            if (carrier.Texture(layout.SlotAtRank(rank)) >= 0)
            {
                textured--;
            }
        }

        return textured;
    }

    private static CapacityInfo Capacity(PixelBuffer image, RgbLsbCarrier carrier, long containerBytes, EmbedOptions options) =>
        new(image.Width, image.Height, carrier.UsablePixels, options.BitsPerChannel, Math.Max(0, containerBytes));

    private static void RequireCapacity(long containerBytes, long contentLength, EmbedOptions options)
    {
        var needed = ContainerOverhead + contentLength;
        if (needed > containerBytes)
        {
            throw new StegoException(
                StegoError.InsufficientCapacity,
                $"Needs {needed:N0} bytes but the image holds {Math.Max(0, containerBytes):N0} at {options.BitsPerChannel} bit(s) per channel{(options.Adaptive ? " (adaptive)" : string.Empty)}.");
        }
    }

    private static Payload Deserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            return PayloadSerializer.Deserialize(body);
        }
        catch (InvalidDataException ex)
        {
            throw new StegoException(StegoError.Corrupted, $"Hidden data is damaged: {ex.Message}", ex);
        }
    }

    private static RevealResult FromContainer(IStegoCarrier carrier, OpenedContainer container, HidingMode mode, KdfProfile? profile)
    {
        var header = container.Header;
        var info = new HiddenDataInfo(2, mode, header.BitsPerSlot, header.Adaptive, profile);
        var content = V2Container.ReadContent(carrier, container);
        try
        {
            return header.Kind == ContentKind.Payload
                ? new RevealResult(info, Deserialize(content), null)
                : new RevealResult(info, null, ShamirShare.Decode(content));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private PixelBuffer EmbedPassword(PixelBuffer cover, byte[] content, ContentKind kind, ReadOnlySpan<char> password, KeyFile? keyFile, EmbedOptions options)
    {
        var stego = cover.Clone();
        var layout = new V2Layout(new RgbLsbCarrier(stego));
        RequireCapacity(ContainerBytes(layout, V2Layout.PublicSlots, ContainerPlacement.Whole, options), content.Length, options);

        var publicBytes = NewPasswordPublicBytes(options.KdfProfile);
        var root = PasswordRoot(password, keyFile, publicBytes.AsSpan(0, ProfileCode.SaltSize), options.KdfProfile);
        try
        {
            layout.WriteRanks(0, publicBytes, new RandomBits(_random));
            V2Container.Write(
                layout, V2Layout.PublicSlots, ContainerPlacement.Whole, root, kind, content, options.BitsPerChannel, options.Adaptive, fill: false, _random);
            return stego;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    /// <summary>A profile-selecting salt followed by 16 random filler bytes.</summary>
    private byte[] NewPasswordPublicBytes(KdfProfile profile)
    {
        var publicBytes = new byte[V2Layout.PublicBytes];
        ProfileCode.NewSalt(publicBytes, profile, _random);
        _random.Fill(publicBytes.AsSpan(ProfileCode.SaltSize));
        return publicBytes;
    }

    private byte[] PasswordRoot(ReadOnlySpan<char> password, KeyFile? keyFile, ReadOnlySpan<byte> salt, KdfProfile profile)
    {
        var master = KeyDerivation.Argon2Master(password, salt, _profiles[profile]);
        try
        {
            return KeyDerivation.PasswordRoot(master, keyFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        _random.Fill(bytes);
        return bytes;
    }

    private static Payload PayloadOf(RevealResult result) =>
        result.Payload ?? throw new StegoException(
            StegoError.SplitShare,
            $"This image holds share {result.Share!.Index} of a {result.Share.Threshold}-of-{result.Share.ShareCount} split. Reveal at least {result.Share.Threshold} of the images and combine their shares.");

    private static StegoException NotFound() =>
        new(StegoError.NotFoundOrWrongPassword, "No hidden data found, or the password is wrong.");

    private static void ValidateBits(int bitsPerChannel)
    {
        if (!SupportedBitsPerChannel.Contains(bitsPerChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerChannel), bitsPerChannel, "Bits per channel must be 1 to 4.");
        }
    }

    private static void RequirePassword(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("A password is required.", nameof(password));
        }
    }
}
