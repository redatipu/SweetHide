using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Server;

/// <summary>Stable, machine-readable error codes sent as the "code" member of every problem+json response.</summary>
internal static class ProblemCodes
{
    public const string BadRequest = "bad-request";
    public const string MissingField = "missing-field";
    public const string InvalidField = "invalid-field";
    public const string NothingToHide = "nothing-to-hide";
    public const string InvalidText = "invalid-text";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string RequestTooLarge = "request-too-large";
    public const string InvalidImage = "invalid-image";
    public const string InsufficientCapacity = "insufficient-capacity";
    public const string NotFoundOrWrongPassword = "not-found-or-wrong-password";
    public const string Corrupted = "corrupted";
    public const string Unauthorized = "unauthorized";
    public const string CrossSite = "cross-site-request";
    public const string RateLimited = "rate-limited";
    public const string Busy = "busy";
    public const string Timeout = "timeout";
    public const string OutOfMemory = "out-of-memory";
    public const string NotFound = "not-found";
    public const string InternalError = "internal-error";
}

/// <summary>A failure that should reach the client as problem+json with the given status and code.</summary>
internal sealed class ApiProblemException : Exception
{
    public ApiProblemException()
    {
    }

    public ApiProblemException(string message)
        : base(message)
    {
    }

    public ApiProblemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ApiProblemException(int status, string code, string title, string? detail = null, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(title)
    {
        Status = status;
        Code = code;
        Detail = detail;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public int Status { get; } = StatusCodes.Status400BadRequest;

    public string Code { get; } = ProblemCodes.BadRequest;

    public string? Detail { get; }

    public IReadOnlyDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();

    /// <summary>Sent as a Retry-After header when set.</summary>
    public int? RetryAfterSeconds { get; init; }

    public static ApiProblemException MissingField(string field) => new(
        StatusCodes.Status400BadRequest,
        ProblemCodes.MissingField,
        $"The form field '{field}' is required.",
        extensions: new Dictionary<string, object?> { ["field"] = field });

    public static ApiProblemException InvalidField(string field, string detail) => new(
        StatusCodes.Status400BadRequest,
        ProblemCodes.InvalidField,
        $"The form field '{field}' is invalid.",
        detail,
        new Dictionary<string, object?> { ["field"] = field });

    public static ApiProblemException RequestTooLarge(long limit) => new(
        StatusCodes.Status413PayloadTooLarge,
        ProblemCodes.RequestTooLarge,
        "The request is too large.",
        $"The limit is {ByteSize.Format(limit)}.",
        new Dictionary<string, object?> { ["limitBytes"] = limit });
}

/// <summary>
/// Turns every exception into problem+json. Expected failures get a specific status and code; anything else becomes
/// a bare 500 with no exception details.
/// </summary>
internal sealed partial class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>One fixed message: "no data" and "wrong password" are indistinguishable by design.</summary>
    public const string NotFoundTitle = "Nothing found, or the password is wrong.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = Map(exception);
        if (problem.Status == StatusCodes.Status500InternalServerError)
        {
            LogUnexpected(logger, exception);
        }

        if (exception is ApiProblemException { RetryAfterSeconds: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    public static ProblemDetails Create(int status, string code, string title, string? detail = null, IEnumerable<KeyValuePair<string, object?>>? extensions = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        foreach (var (key, value) in extensions ?? [])
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    internal static ProblemDetails Map(Exception exception) => exception switch
    {
        ApiProblemException api => Create(api.Status, api.Code, api.Message, api.Detail, api.Extensions),
        StegoException { Error: StegoError.NotFoundOrWrongPassword } => Create(
            StatusCodes.Status422UnprocessableEntity, ProblemCodes.NotFoundOrWrongPassword, NotFoundTitle),
        StegoException { Error: StegoError.Corrupted } => Create(
            StatusCodes.Status422UnprocessableEntity,
            ProblemCodes.Corrupted,
            "The password is right, but the hidden data is damaged.",
            "The image was probably edited or re-compressed after the data was hidden."),
        StegoException { Error: StegoError.InsufficientCapacity } stego => Create(
            StatusCodes.Status422UnprocessableEntity, ProblemCodes.InsufficientCapacity, "The data does not fit in this image.", stego.Message),
        InvalidImageException image => Create(
            StatusCodes.Status422UnprocessableEntity, ProblemCodes.InvalidImage, "The image could not be read.", image.Message),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => Create(
            StatusCodes.Status413PayloadTooLarge, ProblemCodes.RequestTooLarge, "The request is too large."),
        BadHttpRequestException bad => Create(bad.StatusCode, ProblemCodes.BadRequest, "The request could not be read."),
        InvalidDataException => Create(
            StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "The form data is malformed or exceeds a limit."),

        // A big image on a small box (or a container memory limit) can fail one large allocation; nothing is corrupted
        // by that, and the next, smaller request works.
        OutOfMemoryException => Create(
            StatusCodes.Status503ServiceUnavailable,
            ProblemCodes.OutOfMemory,
            "The server ran out of memory for this image.",
            "Try a smaller image, or give the server more memory (see MaxImagePixels and MaxConcurrentJobs)."),
        _ => Create(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "An unexpected error occurred."),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while handling a request")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
