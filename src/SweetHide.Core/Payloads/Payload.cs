namespace SweetHide.Core.Payloads;

/// <summary>Everything hidden in one image: any mix of text messages and files.</summary>
public sealed class Payload
{
    public Payload(IEnumerable<PayloadEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A payload needs at least one entry.", nameof(entries));
        }

        if (list.Count > PayloadSerializer.MaxEntries)
        {
            throw new ArgumentException($"A payload can hold at most {PayloadSerializer.MaxEntries} entries.", nameof(entries));
        }

        Entries = list.AsReadOnly();
    }

    public IReadOnlyList<PayloadEntry> Entries { get; }

    /// <summary>Sum of entry sizes before compression.</summary>
    public long ContentLength => Entries.Sum(e => (long)e.Length);

    public static Payload FromText(string text) => new([PayloadEntry.FromText(text)]);
}
