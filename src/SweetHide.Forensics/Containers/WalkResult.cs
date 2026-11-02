namespace SweetHide.Forensics.Containers;

/// <summary>What a format walker found: structure items, where the container logically ends, and any problems.</summary>
/// <param name="maxItems">Items recorded before the list is capped.</param>
/// <param name="fileLength">Every recorded item is clamped to this length, whatever the file claims.</param>
internal sealed class WalkResult(int maxItems, long fileLength)
{
    private bool _capped;

    public List<StructureItem> Items { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>Offset just past the container's logical end; null when the end marker was never reached.</summary>
    public long? End { get; set; }

    public CorrectedDimensions? CorrectedDimensions { get; set; }

    public static WalkResult Empty { get; } = new(0, 0);

    public void Add(StructureItem item)
    {
        if (Items.Count >= maxItems)
        {
            if (!_capped)
            {
                _capped = true;
                Warnings.Add($"More than {maxItems} structure items; the rest are not listed.");
            }

            return;
        }

        // Consumers slice the file with these ranges, so they must never point outside it.
        var offset = Math.Clamp(item.Offset, 0, fileLength);
        var dataOffset = Math.Clamp(item.DataOffset, 0, fileLength);
        var length = Math.Clamp(item.Length, 0, fileLength - offset);
        var dataLength = Math.Clamp(item.DataLength, 0, fileLength - dataOffset);
        if (length != item.Length || dataLength != item.DataLength || offset != item.Offset || dataOffset != item.DataOffset)
        {
            item = item with
            {
                Offset = offset,
                Length = length,
                DataOffset = dataOffset,
                DataLength = dataLength,
                Traits = item.Traits | StructureTraits.Truncated | StructureTraits.Suspicious,
            };
        }

        Items.Add(item);
    }

    public void Warn(string message)
    {
        // Hostile files can repeat the same problem endlessly; a handful of warnings is enough.
        if (Warnings.Count < 64)
        {
            Warnings.Add(message);
        }
    }
}
