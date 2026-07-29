using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace MageRide.Reputation.Grpc;

/// <summary>
/// Rejects a gRPC call that does not carry the configured internal key.
/// </summary>
/// <remarks>
/// <para>
/// D3' puts <c>reputation.v1</c> on service-to-service mTLS and the API gateway routes only the two
/// <c>/v1/admin</c> HTTP paths at this service, so the gRPC port is never reachable from outside
/// the cluster. Until the mesh lands (C042) the in-cluster hop is guarded by a shared secret — the
/// same interim ride-svc's <c>/v1/internal/**</c> family uses, carried as
/// <c>x-mageride-internal-key</c> metadata.
/// </para>
/// <para>
/// The answer is <see cref="StatusCode.NotFound"/> rather than <c>PermissionDenied</c>, matching
/// what the gateway returns for the internal HTTP prefix: a caller who is not entitled to the
/// internal plane should not be able to map it. The comparison is fixed-time, because a
/// length-varying compare leaks the secret a character at a time.
/// </para>
/// </remarks>
public sealed class InternalKeyInterceptor : Interceptor
{
    /// <summary>Metadata key carrying <c>Reputation:InternalApiKey</c>. Replaced by the mesh identity in C042.</summary>
    public const string MetadataKey = "x-mageride-internal-key";

    private readonly byte[] _expected;

    public InternalKeyInterceptor(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _expected = Encoding.UTF8.GetBytes(apiKey);
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(continuation);

        var presented = context.RequestHeaders.GetValue(MetadataKey) ?? string.Empty;

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "No such service."));
        }

        return continuation(request, context);
    }
}
