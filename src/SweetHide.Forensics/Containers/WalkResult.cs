namespace SweetHide.Forensics.Containers;

/// <summary>What a format walker found: structure items, where the container logically ends, and any problems.</summary>
internal sealed class WalkResult(int maxItems)
{
    private bool _capped;

    public List<StructureItem> Items { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>Offset just past the container's logical end; null when the end marker was never reached.</summary>
    public long? End { get; set; }

    public CorrectedDimensions? CorrectedDimensions { get; set; }

    public static WalkResult Empty { get; } = new(0);

    public void Add(StructureItem item)
    {
        if (Items.Count < maxItems)
        {
            Items.Add(item);
        }
        else if (!_capped)
        {
            _capped = true;
            Warnings.Add($"More than {maxItems:N0} structure items; the rest are not listed.");
        }
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
