using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;

namespace SweetHide.Cli;

/// <summary>Everything a command touches outside its arguments, so tests can substitute it.</summary>
internal sealed class CliContext
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required IPasswordProvider Passwords { get; init; }

    public IImageCodec Codec { get; init; } = new SkiaImageCodec();

    public KdfParameters Kdf { get; init; } = KdfParameters.Default;

    /// <summary>Raw standard input, for <c>-i -</c> and <c>--text-file -</c>.</summary>
    public Func<Stream> OpenStandardInput { get; init; } = Console.OpenStandardInput;

    /// <summary>Raw standard output, for <c>-o -</c> (the PNG goes here, so messages move to stderr).</summary>
    public Func<Stream> OpenStandardOutput { get; init; } = Console.OpenStandardOutput;

    /// <summary>Print machine-readable JSON on stdout instead of human text.</summary>
    public bool Json { get; set; }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NotFoundOrWrongPassword = 2;
    public const int InsufficientCapacity = 3;
    public const int Corrupted = 4;
    public const int UnsupportedFormat = 5;
    public const int SplitShare = 6;
    public const int NotEnoughShares = 7;
    public const int SharesDoNotMatch = 8;
}

/// <summary>A failure with a message meant for the user and a specific exit code.</summary>
internal sealed class CliException : Exception
{
    public CliException()
    {
    }

    public CliException(string message)
        : base(message)
    {
    }

    public CliException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CliException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; } = ExitCodes.Error;
}
