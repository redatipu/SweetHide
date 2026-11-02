using Magi.Core.Crypto;
using Magi.Core.Imaging;

namespace Magi.Cli;

/// <summary>Everything a command touches outside its arguments, so tests can substitute it.</summary>
internal sealed class CliContext
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required IPasswordProvider Passwords { get; init; }

    public IImageCodec Codec { get; init; } = new SkiaImageCodec();

    public KdfParameters Kdf { get; init; } = KdfParameters.Default;
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NotFoundOrWrongPassword = 2;
    public const int InsufficientCapacity = 3;
    public const int Corrupted = 4;
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
