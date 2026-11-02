namespace SweetHide.Core;

public enum StegoError
{
    Unknown = 0,

    /// <summary>The payload does not fit in the cover image at the chosen bits per channel.</summary>
    InsufficientCapacity,

    /// <summary>
    /// Nothing was found. Deliberately covers both "no hidden data" and "wrong password": the two are
    /// indistinguishable by design.
    /// </summary>
    NotFoundOrWrongPassword,

    /// <summary>The password was right but the hidden data was damaged (e.g. the image was edited or re-encoded).</summary>
    Corrupted,

    /// <summary>The password was right but the data was written by a newer version of SweetHide.</summary>
    UnsupportedFormat,

    /// <summary>
    /// The image holds one share of a payload split across several images. Use <see cref="StegoEngine.Reveal(Imaging.PixelBuffer, ReadOnlySpan{char}, Crypto.KeyFile?)"/>
    /// on enough of them and <see cref="StegoEngine.CombineShares"/>.
    /// </summary>
    SplitShare,

    /// <summary>Fewer shares than the split's threshold were given.</summary>
    NotEnoughShares,

    /// <summary>The shares come from different splits (or two different shares claim the same number).</summary>
    SharesDoNotMatch,
}

public sealed class StegoException : Exception
{
    public StegoException()
    {
    }

    public StegoException(string message)
        : base(message)
    {
    }

    public StegoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StegoException(StegoError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public StegoError Error { get; }
}
