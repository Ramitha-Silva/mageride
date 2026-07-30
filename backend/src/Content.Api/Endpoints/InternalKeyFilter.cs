using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;

namespace MageRide.Content.Endpoints;

/// <summary>
/// Rejects a call that does not carry <c>Content:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// <para>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c> prefix
/// (C008): a caller who is not entitled to the internal plane should not be able to map it. The
/// comparison is fixed-time — a length-varying compare leaks the key a character at a time.
/// </para>
/// <para>
/// <b>A null key means "no check", and only the template render is mapped that way.</b> Every other
/// internal family on the platform is unmapped without its key; this one cannot be, because
/// unmapping it stops notification-svc rendering anything and the failure would surface there rather
/// than here. <c>ContentApplication</c> says so loudly at start-up. The purge route <i>is</i>
/// unmapped without the key, because that one is a write.
/// </para>
/// </remarks>
internal sealed class InternalKeyFilter : IEndpointFilter
{
    private readonly byte[]? _expected;

    public InternalKeyFilter(string? apiKey) =>
        _expected = string.IsNullOrWhiteSpace(apiKey) ? null : Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (_expected is null)
        {
            return next(context);
        }

        var presented = context.HttpContext.Request.Headers[ContentEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
