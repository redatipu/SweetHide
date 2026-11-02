namespace SweetHide.Core.Crypto;

/// <summary>
/// Argon2id cost parameters. They are not stored in the image, so hiding and revealing must use the same values.
/// </summary>
/// <param name="MemoryKiB">Memory cost in KiB.</param>
/// <param name="Iterations">Time cost (passes over memory).</param>
/// <param name="Parallelism">Lanes. Changing this changes the derived key.</param>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Format v1 defaults: 64 MiB, 3 passes, 4 lanes (OWASP-recommended range).</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 4);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MemoryKiB, 8 * Parallelism, nameof(MemoryKiB));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Iterations, nameof(Iterations));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Parallelism, nameof(Parallelism));
    }
}
