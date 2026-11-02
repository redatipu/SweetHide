namespace SweetHide.Core.Stego;

/// <summary>
/// The medium the container is written into: a fixed, numbered list of slots (samples), each of which can carry a
/// few low bits. The container format (salt, permutation, header, body) only talks to this interface, so the same
/// format can later live in JPEG coefficients or palette indices as well as in RGB values.
/// </summary>
/// <remarks>
/// Every carrier must hold these invariants; the format is unreadable without them:
/// <list type="number">
/// <item><b>Recomputable slots.</b> <see cref="SlotCount"/> and the sample each slot index refers to depend only on
/// properties that <see cref="Write"/> never changes, so a carrier built from the stego output has exactly the same
/// slots in the same order as the one the data was written to. (RGB: alpha is never modified and pixels with
/// alpha 0 are skipped, so the usable pixel set is the same before and after.)</item>
/// <item><b>Exact read-back.</b> After <c>Write(slot, v, bits, coin)</c>, <c>Read(slot, bits) == v</c>, and no
/// other slot's value changes.</item>
/// <item><b>Minimal change.</b> <see cref="Write"/> moves the sample by the smallest amount that gives the wanted
/// low bits, using <c>coin</c> only to break ties (LSB matching rather than replacement).</item>
/// <item><b>Thread safety.</b> Reads and writes of <em>distinct</em> slots may run concurrently.</item>
/// </list>
/// </remarks>
internal interface IStegoCarrier
{
    /// <summary>Number of usable slots.</summary>
    long SlotCount { get; }

    /// <summary>Largest number of low bits a single slot can carry.</summary>
    int MaxBitsPerSlot { get; }

    /// <summary>The low <paramref name="bits"/> bits of a slot.</summary>
    int Read(long slot, int bits);

    /// <summary>Changes a slot minimally so that its low <paramref name="bits"/> bits equal <paramref name="value"/>.</summary>
    void Write(long slot, int value, int bits, bool coin);
}

/// <summary>
/// A carrier that can rank slots by local texture for adaptive embedding (prefer busy regions, where small changes
/// are hardest to detect).
/// </summary>
/// <remarks>
/// The reader has to recompute exactly the texture the writer used, from the stego output. Texture therefore may
/// only depend on <em>reference slots</em> (those for which <see cref="Texture"/> returns -1) and on properties
/// <see cref="IStegoCarrier.Write"/> never changes. The format never writes a reference slot after textures are
/// first used: headers and adaptive bodies avoid them, and only the public region and the recipient table (both
/// written before any texture is computed) may land on them.
/// <para>
/// Low bits above the embedded ones are <em>not</em> a safe texture source: LSB matching moves 128 to 127 as
/// readily as to 129, which changes every higher bit.
/// </para>
/// </remarks>
internal interface IAdaptiveCarrier : IStegoCarrier
{
    /// <summary>Number of slots that are not reference slots.</summary>
    long TexturedSlotCount { get; }

    /// <summary>Local texture around a slot, 0 (flat) to 255 (busy), or -1 for a reference slot.</summary>
    int Texture(long slot);
}
