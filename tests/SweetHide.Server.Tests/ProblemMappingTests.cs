using Microsoft.AspNetCore.Http;

using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Server.Tests;

public sealed class ProblemMappingTests
{
    public static TheoryData<Exception, int, string> Cases => new()
    {
        { new StegoException(StegoError.NotFoundOrWrongPassword, "internal wording"), 422, "not-found-or-wrong-password" },
        { new StegoException(StegoError.Corrupted, "internal wording"), 422, "corrupted" },
        { new StegoException(StegoError.InsufficientCapacity, "Needs 9 bytes"), 422, "insufficient-capacity" },
        { new InvalidImageException("Unsupported or corrupt image file."), 422, "invalid-image" },
        { new BadHttpRequestException("too big", StatusCodes.Status413PayloadTooLarge), 413, "request-too-large" },
        { new BadHttpRequestException("bad framing", StatusCodes.Status400BadRequest), 400, "bad-request" },
        { new InvalidDataException("Form value count limit 1024 exceeded."), 400, "bad-request" },
        { new InsufficientMemoryException("a large array"), 503, "out-of-memory" }, // an OutOfMemoryException
        { new InvalidOperationException("secret internals"), 500, "internal-error" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exceptions_map_to_status_and_code(Exception exception, int status, string code)
    {
        var problem = ProblemExceptionHandler.Map(exception);

        Assert.Equal(status, problem.Status);
        Assert.Equal(code, problem.Extensions["code"]);
    }

    [Fact]
    public void Wrong_password_never_echoes_the_engine_message()
    {
        var problem = ProblemExceptionHandler.Map(new StegoException(StegoError.NotFoundOrWrongPassword, "internal wording"));

        Assert.Equal(ProblemExceptionHandler.NotFoundTitle, problem.Title);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public void Unexpected_errors_reveal_nothing_about_the_exception()
    {
        var problem = ProblemExceptionHandler.Map(new InvalidOperationException("secret internals"));

        Assert.DoesNotContain("secret", problem.Title, StringComparison.Ordinal);
        Assert.Null(problem.Detail);
        Assert.Single(problem.Extensions);
    }
}
