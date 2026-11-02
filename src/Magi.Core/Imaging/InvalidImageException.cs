namespace Magi.Core.Imaging;

/// <summary>The input could not be decoded as a supported image, or exceeds the configured size limits.</summary>
public sealed class InvalidImageException : Exception
{
    public InvalidImageException()
    {
    }

    public InvalidImageException(string message)
        : base(message)
    {
    }

    public InvalidImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
