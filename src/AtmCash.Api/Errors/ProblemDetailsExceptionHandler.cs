using AtmCash.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Errors;

/// <summary>
/// Turns exceptions into RFC 9457 ProblemDetails. Every response carries a stable
/// machine-readable <c>code</c> extension next to the human-readable <c>detail</c>.
/// </summary>
internal sealed partial class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InternalError = "INTERNAL_ERROR";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = exception switch
        {
            DomainException domain => (StatusFor(domain.Kind), domain.Code, TitleFor(domain.Kind), domain.Message),
            BadHttpRequestException badRequest =>
                (StatusCodes.Status400BadRequest, InvalidRequest, "Invalid request", badRequest.Message),
            _ => (StatusCodes.Status500InternalServerError, InternalError, "Unexpected error", "An unexpected error occurred."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = $"https://httpstatuses.io/{status}",
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Validation failed",
        ErrorKind.NotFound => "Resource not found",
        ErrorKind.Conflict => "Request conflicts with current state",
        _ => "Unexpected error",
    };
}
