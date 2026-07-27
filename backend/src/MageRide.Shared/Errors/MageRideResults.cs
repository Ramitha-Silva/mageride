using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Shared.Errors;

/// <summary>
/// Minimal-API result helpers that emit the D3' §0 error envelope. Prefer these over
/// <c>Results.BadRequest()</c> and friends — those produce a body without a registry
/// <c>type</c> URI.
/// </summary>
public static class MageRideResults
{
    /// <summary>An RFC 7807 response for a registry code.</summary>
    public static IResult Problem(ErrorCode error, string? detail = null, params KeyValuePair<string, object?>[] extensions)
    {
        ArgumentNullException.ThrowIfNull(error);

        // Instance and traceId are stamped by the shared ProblemDetails customiser, which sees
        // the HttpContext this result is executed against.
        var problem = MageRideProblem.Create(error, detail, instance: null, extensions);
        return TypedResults.Problem(problem);
    }

    /// <summary>400 <c>validation-failed</c> with a field → messages map.</summary>
    public static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors, string? detail = null) =>
        TypedResults.Problem(MageRideProblem.Validation(errors, detail));

    /// <summary>Sugar for the single-field case.</summary>
    public static IResult ValidationProblem(string field, string message) =>
        ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
