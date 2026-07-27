using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// Gives every request an id that survives the whole edge→service→response path, and hands it back
/// on the response so a support ticket can name one request out of a day's traffic.
/// </summary>
/// <remarks>
/// <para>
/// <c>X-Request-Id</c> is accepted from the caller when it is well formed and generated otherwise.
/// The W3C <c>traceparent</c> is <em>not</em> managed here — ASP.NET Core's hosting layer already
/// parses an inbound one into <see cref="Activity.Current"/>, and
/// <see cref="GatewayTransforms"/> writes the gateway's own span onto the forwarded request.
/// </para>
/// <para>
/// The response header is written from an <c>OnStarting</c> callback rather than set directly:
/// <c>UseExceptionHandler</c> clears the response before re-running the pipeline, which would drop
/// a header set up front — precisely on the responses where the id matters most.
/// </para>
/// </remarks>
internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    /// <summary>A caller-supplied id is echoed only if it is short and printable ASCII.</summary>
    private const int MaxRequestIdLength = 128;

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var requestId = Sanitise(context.Request.Headers[HeaderName].ToString())
            ?? Activity.Current?.TraceId.ToString()
            ?? context.TraceIdentifier;

        // Overwrite the inbound header so the value forwarded to the backend is the sanitised one,
        // and so a request that arrived without one still carries it inward.
        context.Request.Headers[HeaderName] = requestId;
        context.TraceIdentifier = requestId;

        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            ctx.Response.Headers[HeaderName] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }

    private static string? Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxRequestIdLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            // Header-splitting defence and log hygiene: an id is echoed into a response header and
            // into every log line for this request.
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))
            {
                return null;
            }
        }

        return value;
    }
}
