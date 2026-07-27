using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.Shared.Errors;

/// <summary>
/// Terminal exception handler: every unhandled exception leaves the process as
/// <c>application/problem+json</c> with a registry <c>type</c> URI (D3' §0).
/// </summary>
/// <remarks>
/// Only <see cref="MageRideException"/> reaches the client with its own code and detail. Anything
/// else becomes an opaque <c>internal-error</c> — the message may contain connection strings,
/// SQL or PII, so it is logged, never serialised. Outside Development, no stack trace is exposed.
/// </remarks>
public sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // A cancelled request has no client left to answer.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (error, detail, extensions) = Describe(exception);

        if (error.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path} -> {Code}",
                httpContext.Request.Method, httpContext.Request.Path, error.Code);
        }
        else
        {
            logger.LogInformation("Request failed on {Method} {Path} -> {Status} {Code}",
                httpContext.Request.Method, httpContext.Request.Path, error.Status, error.Code);
        }

        if (httpContext.Response.HasStarted)
        {
            // The body is already on the wire; the framework will just drop the connection.
            return false;
        }

        httpContext.Response.StatusCode = error.Status;

        var problem = MageRideProblem.Create(httpContext, error, detail, extensions);

        if (environment.IsDevelopment() && error.Status >= StatusCodes.Status500InternalServerError)
        {
            problem.Extensions["exception"] = exception.ToString();
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    private static (ErrorCode Error, string? Detail, IEnumerable<KeyValuePair<string, object?>>? Extensions) Describe(Exception exception) =>
        exception switch
        {
            MageRideValidationException validation =>
                (validation.Error, validation.Detail,
                    Merge(validation.Extensions, MageRideProblem.ErrorsExtension, validation.Errors)),

            MageRideException known => (known.Error, known.Detail, known.Extensions),

            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (MageRideErrors.PayloadTooLarge, null, null),

            BadHttpRequestException => (MageRideErrors.BadRequest, null, null),

            TimeoutException => (MageRideErrors.UpstreamTimeout, null, null),

            // A cancellation that is not the client giving up is a server-side timeout.
            OperationCanceledException => (MageRideErrors.UpstreamTimeout, null, null),

            _ => (MageRideErrors.InternalError, null, null),
        };

    private static IEnumerable<KeyValuePair<string, object?>> Merge(
        Dictionary<string, object?> extensions, string key, object? value)
    {
        var merged = new Dictionary<string, object?>(extensions) { [key] = value };
        return merged;
    }
}
