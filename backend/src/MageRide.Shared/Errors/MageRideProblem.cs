using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MageRide.Shared.Errors;

/// <summary>
/// Builds the one error shape MageRide emits (D3' §0):
/// <code>
/// { "type":"https://mageride.lk/errors/{code}", "title":"...", "status":400,
///   "detail":"...", "instance":"/path", "traceId":"00-..." }
/// </code>
/// Always served as <c>application/problem+json</c>.
/// </summary>
public static class MageRideProblem
{
    /// <summary>Extension member carrying the W3C trace id, per the D3' §0 example.</summary>
    public const string TraceIdExtension = "traceId";

    /// <summary>Extension member carrying field-level failures on <c>validation-failed</c>.</summary>
    public const string ErrorsExtension = "errors";

    public static ProblemDetails Create(
        ErrorCode error,
        string? detail = null,
        string? instance = null,
        IEnumerable<KeyValuePair<string, object?>>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = new ProblemDetails
        {
            Type = error.TypeUri,
            Title = error.Title,
            Status = error.Status,
            Detail = detail,
            Instance = instance,
        };

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return problem;
    }

    /// <summary>Creates the problem and fills <c>instance</c> and <c>traceId</c> from the request.</summary>
    public static ProblemDetails Create(
        HttpContext context,
        ErrorCode error,
        string? detail = null,
        IEnumerable<KeyValuePair<string, object?>>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = Create(error, detail, context.Request.Path.HasValue ? context.Request.Path.Value : null, extensions);
        Enrich(context, problem);
        return problem;
    }

    /// <summary>400 <c>validation-failed</c> with an <c>errors</c> map of field → messages.</summary>
    public static ProblemDetails Validation(
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null,
        string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var problem = Create(MageRideErrors.ValidationFailed, detail, instance);
        problem.Extensions[ErrorsExtension] = errors;
        return problem;
    }

    /// <summary>
    /// Stamps <c>instance</c> (when absent) and the W3C <c>traceId</c> onto an existing problem —
    /// including problems produced by the framework rather than by us.
    /// </summary>
    public static void Enrich(HttpContext context, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        problem.Instance ??= context.Request.Path.HasValue ? context.Request.Path.Value : null;
        problem.Status ??= context.Response.StatusCode;

        // A framework-generated problem has no MageRide type URI; give it the kernel code for
        // its status so every response on the wire carries a registry key.
        if (string.IsNullOrEmpty(problem.Type) || !problem.Type.StartsWith(MageRideErrors.TypeUriBase, StringComparison.Ordinal))
        {
            var fallback = MageRideErrors.ForStatus(problem.Status ?? StatusCodes.Status500InternalServerError);
            problem.Type = fallback.TypeUri;
            problem.Title ??= fallback.Title;
        }

        problem.Extensions[TraceIdExtension] = Activity.Current?.Id ?? context.TraceIdentifier;
    }
}
