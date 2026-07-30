using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.PositionProcessor.Throttling;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Processor = MageRide.HotPath.PositionProcessor.Processing.PositionProcessor;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// Builds position-processor-svc's parts against a live Redis, without a host.
/// </summary>
/// <remarks>
/// The gate tests drive these directly rather than through the consumer, so an assertion about
/// <i>what</i> a sample does cannot be confused with one about <i>whether</i> a consumer delivered
/// it — the pipeline tests answer that. Everything built here is the real type with real Redis
/// behind it; nothing is a stub except the publisher, and only where a test has switched the
/// producer off.
/// </remarks>
internal static class ProcessorParts
{
    /// <summary>Options with every gate on and the shipped defaults, before a test tweaks them.</summary>
    public static PositionProcessorOptions Defaults() => new();

    public static IOptions<PositionProcessorOptions> Wrap(PositionProcessorOptions? options) =>
        Options.Create(options ?? Defaults());

    public static LivePositionIndex Index(
        IConnectionMultiplexer redis, PositionProcessorOptions? options = null) =>
        new(redis, Wrap(options), NullLogger<LivePositionIndex>.Instance);

    public static DriverAvailabilityIndex Availability(
        IConnectionMultiplexer redis, PositionProcessorOptions? options = null) =>
        new(redis, Wrap(options), NullLogger<DriverAvailabilityIndex>.Instance);

    public static PlausibilityFilter Filter(PositionProcessorOptions? options = null) =>
        new(Wrap(options));

    public static IngestRateGuard RateGuard(
        IConnectionMultiplexer redis,
        IEventPublisher publisher,
        PositionProcessorOptions? options = null,
        TimeProvider? clock = null) =>
        new(redis, publisher, Wrap(options), clock ?? TimeProvider.System, NullLogger<IngestRateGuard>.Instance);

    /// <summary>The whole processor, wired the way <c>PositionProcessorApplication</c> wires it.</summary>
    public static Processor Build(
        IConnectionMultiplexer redis,
        IEventPublisher? publisher = null,
        PositionProcessorOptions? options = null,
        TimeProvider? clock = null)
    {
        var settings = options ?? Defaults();
        var events = publisher ?? new UnusedPublisher();
        var time = clock ?? TimeProvider.System;

        return new Processor(
            Index(redis, settings),
            Availability(redis, settings),
            Filter(settings),
            RateGuard(redis, events, settings, time),
            events,
            Wrap(settings),
            time,
            NullLogger<Processor>.Instance);
    }

    /// <summary>
    /// Fails loudly if a test that turned <c>PublishNormalized</c> off ever reaches the producer.
    /// A no-op stub would let a regression in that flag pass unnoticed.
    /// </summary>
    /// <remarks>
    /// D-17's <c>mqtt.rate_violation</c> goes through the same interface, so a test that expects one
    /// has to pass a real publisher (or <see cref="CollectingPublisher"/>) rather than this.
    /// </remarks>
    public sealed class UnusedPublisher : IEventPublisher
    {
        public Task<PublishReceipt> PublishAsync(EventMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                $"This test expected nothing to be published; got '{message?.Topic}'.");

        public Task<IReadOnlyList<PublishReceipt>> PublishAsync(
            IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This test expected nothing to be published.");
    }

    /// <summary>Records what was published, for the assertions that are about the event itself.</summary>
    /// <remarks>
    /// Used only where the broker is not what is under test. The D-17 audit event is also asserted
    /// end-to-end off a real Redpanda topic, because "the envelope is right" and "it reaches
    /// <c>audit.events</c>" are two claims.
    /// </remarks>
    public sealed class CollectingPublisher : IEventPublisher
    {
        private readonly Lock _gate = new();
        private readonly List<EventMessage> _messages = [];

        public IReadOnlyList<EventMessage> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public Task<PublishReceipt> PublishAsync(
            EventMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            lock (_gate)
            {
                _messages.Add(message);
            }

            return Task.FromResult(new PublishReceipt(message.Topic, Partition: 0, Offset: _messages.Count));
        }

        public async Task<IReadOnlyList<PublishReceipt>> PublishAsync(
            IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var receipts = new List<PublishReceipt>(messages.Count);

            foreach (var message in messages)
            {
                receipts.Add(await PublishAsync(message, cancellationToken));
            }

            return receipts;
        }
    }
}
