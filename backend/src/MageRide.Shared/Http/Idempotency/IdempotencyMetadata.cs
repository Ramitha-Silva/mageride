using Microsoft.AspNetCore.Builder;

namespace MageRide.Shared.Http.Idempotency;

/// <summary>Endpoint-level override of the default <c>Idempotency-Key</c> requirement.</summary>
/// <param name="Required">
/// <see langword="false"/> takes the endpoint out of the idempotency pipeline entirely — no key
/// demanded, no response captured.
/// </param>
public sealed record IdempotencyMetadata(bool Required)
{
    public static readonly IdempotencyMetadata Enabled = new(true);
    public static readonly IdempotencyMetadata Disabled = new(false);
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Opts an endpoint out of idempotent replay. Reserved for surfaces that carry their own
    /// dedupe key and cannot supply the header — payment-provider webhooks, which key on
    /// <c>provider_transaction_id</c> (R-19, E-05), and MQTT/Kafka-triggered internal routes.
    /// </summary>
    public static TBuilder AllowMissingIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(IdempotencyMetadata.Disabled);
        return builder;
    }

    /// <summary>
    /// Demands an <c>Idempotency-Key</c> on this endpoint regardless of method. POST already
    /// requires one by default (D3' §0).
    /// </summary>
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(IdempotencyMetadata.Enabled);
        return builder;
    }
}
