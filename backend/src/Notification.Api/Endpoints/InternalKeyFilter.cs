using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;

namespace MageRide.Notification.Endpoints;

/// <summary>
/// Rejects a call that does not carry <c>Notification:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c>
/// prefix (C008): a caller who is not entitled to the internal plane should not be able to map it.
/// The comparison is fixed-time — a length-varying compare leaks the key a character at a time.
/// <para>
/// There is no "null key means no check" mode here, unlike content-svc's filter. Without the key the
/// whole family is left unmapped by <c>NotificationApplication</c>, because this plane sends rather
/// than reads.
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

        var presented = context.HttpContext.Request.Headers[InternalNotifyEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
