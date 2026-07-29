using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using Microsoft.Extensions.Logging;

namespace MageRide.Reputation.Messaging;

/// <summary>
/// Turns a <c>ride.events</c> envelope into the facts it implies, and counts them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the live intake, not the gRPC reports.</b> D3' declares
/// <c>ReportCancellation</c>/<c>ReportNoShow</c> on the proto and ride-svc as their caller, but
/// ride-svc (C032) is built and calls nothing — it publishes <c>ride.events</c>, which D6' §2.1
/// lists this service as a consumer of, and CLAUDE.md's universal rule is that cross-service state
/// changes travel through the outbox and not through direct calls. Both paths therefore exist and
/// both are implemented; they meet at <c>reputation.intake_log</c>, so a platform that later wires
/// the gRPC side as well counts each fact once and not twice.
/// </para>
/// <para>
/// A completed ride produces <b>two</b> facts, one per side. D5' §7.2 says "the counter resets to 0
/// on any completed ride" without naming a role, and it has to reset both: a driver's consecutive
/// run is reset by finishing a job for the same reason a passenger's is. The pair is also what the
/// E-07 pair-frequency detector reads, which is why both rows carry the ride id.
/// </para>
/// </remarks>
public interface IRideEventHandler
{
    Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideEventHandler"/>
public sealed class RideEventHandler(
    IReputationService reputation, ILogger<RideEventHandler> logger) : IRideEventHandler
{
    public async Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        foreach (var fact in Interpret(envelope))
        {
            var outcome = await reputation.RecordAsync(fact, cancellationToken);

            logger.LogDebug(
                "{EventType} on ride {RideId}: {Subject} ({Role}) → {State}{Duplicate}",
                envelope.EventType, envelope.RideId, fact.SubjectId, fact.SubjectRole,
                outcome.Status.State, outcome.Duplicate ? " (already counted)" : string.Empty);
        }
    }

    /// <summary>
    /// The event → fact table. Pure, so the mapping can be asserted without a database.
    /// </summary>
    internal static IReadOnlyList<ReputationFact> Interpret(RideEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;

        return envelope.EventType switch
        {
            // Both sides, so both consecutive runs reset and the pair is recorded for E-07.
            RideEventTypes.Completed => Both(envelope, IntakeKinds.Completion),

            // §11.12 gives the rider three cancellation rows and only two of them count: the
            // pre-acceptance one is explicitly exempt (D5' §7.2), so it is skipped entirely rather
            // than recorded uncounted — there is no counter it could ever move and no appeal that
            // would ask about it.
            RideEventTypes.Cancelled when
                RideReasonCodes.IsPostAcceptanceRiderCancel(payload?.ReasonCode) && payload?.PassengerId is { } passenger =>
                [Fact(envelope, IntakeKinds.Cancellation, passenger, SubjectRoles.Passenger)],

            // ride-svc emits this alongside ride.cancelled for a driver-side cancel, which is why
            // the DRIVER_* reason codes are not also read off ride.cancelled: that would count the
            // same cancel twice under two dedupe keys.
            RideEventTypes.DriverCancelled when payload?.DriverId is { } driver =>
                [Fact(envelope, IntakeKinds.Cancellation, driver, SubjectRoles.Driver)],

            RideEventTypes.NoShowRider when payload?.PassengerId is { } noShowPassenger =>
                [Fact(envelope, IntakeKinds.NoShow, noShowPassenger, SubjectRoles.Passenger)],

            RideEventTypes.NoShowDriver when payload?.DriverId is { } noShowDriver =>
                [Fact(envelope, IntakeKinds.NoShow, noShowDriver, SubjectRoles.Driver)],

            _ => [],
        };
    }

    private static IReadOnlyList<ReputationFact> Both(RideEventEnvelope envelope, string kind)
    {
        var facts = new List<ReputationFact>(2);

        if (envelope.Payload?.PassengerId is { } passenger)
        {
            facts.Add(Fact(envelope, kind, passenger, SubjectRoles.Passenger));
        }

        if (envelope.Payload?.DriverId is { } driver)
        {
            facts.Add(Fact(envelope, kind, driver, SubjectRoles.Driver));
        }

        return facts;
    }

    /// <summary>
    /// The dedupe key carries the subject as well as the event id: one envelope can produce a fact
    /// for each side of the ride, and both would otherwise claim the same key — the second would
    /// look like a redelivery and the driver would never be counted.
    /// </summary>
    private static ReputationFact Fact(RideEventEnvelope envelope, string kind, Guid subjectId, string role) =>
        new(
            DedupeKey: $"{IntakeSources.RideEvents}:{envelope.EventId}:{role}",
            Kind: kind,
            SubjectId: subjectId,
            SubjectRole: role,
            RideId: envelope.RideId,
            Source: IntakeSources.RideEvents,
            ReasonCode: envelope.Payload?.ReasonCode,
            SystemInitiated: envelope.Payload?.SystemInitiated ?? false);
}
