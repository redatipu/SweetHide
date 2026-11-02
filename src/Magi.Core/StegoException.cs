namespace Magi.Core;

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
