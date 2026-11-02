namespace SweetHide.Core.Crypto;

/// <summary>
/// Argon2id cost parameters. They are not stored in the image: format v2 only records which
/// <see cref="KdfProfile"/> was used (see <see cref="KdfProfiles"/>), so hiding and revealing must agree on the values
/// behind each profile.
/// </summary>
/// <param name="MemoryKiB">Memory cost in KiB.</param>
/// <param name="Iterations">Time cost (passes over memory).</param>
/// <param name="Parallelism">Lanes. Changing this changes the derived key.</param>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>The <see cref="KdfProfile.Normal"/> profile and the fixed format v1 setting: 64 MiB, 3 passes, 4 lanes.</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 4);

    /// <summary>The <see cref="KdfProfile.Fast"/> profile: 16 MiB, 2 passes, 4 lanes (about 1/6 of the normal cost).</summary>
    public static KdfParameters Fast { get; } = new(16 * 1024, 2, 4);

    /// <summary>The <see cref="KdfProfile.Paranoid"/> profile: 256 MiB, 4 passes, 4 lanes (about 5x the normal cost).</summary>
    public static KdfParameters Paranoid { get; } = new(256 * 1024, 4, 4);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MemoryKiB, 8 * Parallelism, nameof(MemoryKiB));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Iterations, nameof(Iterations));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Parallelism, nameof(Parallelism));
    }
}

/// <summary>How expensive it is to test one password against an image (for the owner and for an attacker alike).</summary>
public enum KdfProfile
{
    /// <summary>For slow devices or low-value secrets. Weakest against password guessing.</summary>
    Fast = 0,

    /// <summary>The default, identical to format v1.</summary>
    Normal = 1,

    /// <summary>For strong protection when revealing may take a second or more and a few hundred MiB of memory.</summary>
    Paranoid = 2,
}

/// <summary>The Argon2id parameters behind each <see cref="KdfProfile"/>.</summary>
/// <remarks>
/// The profile is not written in clear. The writer picks a salt whose hash selects the profile (see docs/FORMAT.md),
/// so the stored salt bits stay uniformly random and the reader runs Argon2id exactly once.
/// </remarks>
public sealed record KdfProfiles(KdfParameters Fast, KdfParameters Normal, KdfParameters Paranoid)
{
    /// <summary><see cref="KdfParameters.Fast"/>, <see cref="KdfParameters.Default"/>, <see cref="KdfParameters.Paranoid"/>.</summary>
    public static KdfProfiles Default { get; } = new(KdfParameters.Fast, KdfParameters.Default, KdfParameters.Paranoid);

    /// <summary>Every profile uses <paramref name="parameters"/> (tests and custom setups).</summary>
    public static KdfProfiles Uniform(KdfParameters parameters) => new(parameters, parameters, parameters);

    public KdfParameters this[KdfProfile profile] => profile switch
    {
        KdfProfile.Fast => Fast,
        KdfProfile.Normal => Normal,
        KdfProfile.Paranoid => Paranoid,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown KDF profile."),
    };

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Fast);
        ArgumentNullException.ThrowIfNull(Normal);
        ArgumentNullException.ThrowIfNull(Paranoid);
        Fast.Validate();
        Normal.Validate();
        Paranoid.Validate();
    }
}
