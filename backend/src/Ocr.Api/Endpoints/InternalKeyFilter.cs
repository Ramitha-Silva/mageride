using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;

namespace MageRide.Ocr.Endpoints;

/// <summary>
/// Rejects a call that does not carry <c>Ocr:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c>
/// prefix (C008): a caller not entitled to the internal plane should not be able to map it. The
/// comparison is fixed-time — a length-varying compare leaks the key a character at a time.
/// <para>
/// There is no "null key means no check" mode. Without the key
/// <see cref="OcrApplication"/> leaves the whole family unmapped, because this service has no other
/// surface at all: what is behind it reads any <c>docs.uploads</c> row by id and returns what is
/// printed on somebody's licence.
/// </para>
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(
        apiKey ?? throw new ArgumentNullException(nameof(apiKey)));

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[ExtractionEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
