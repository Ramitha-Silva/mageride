using MageRide.Safety.Clients;
using MageRide.Safety.Configuration;
using MageRide.Safety.Domain;
using MageRide.Safety.Persistence;
using MageRide.Safety.Sharing;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Sos;

/// <summary>The body of <c>POST /v1/sos</c>, after validation.</summary>
/// <param name="UserId">
/// The authenticated raiser. <see langword="null"/> only on the AL-44 web path, where a share token
/// is the identity (public-bff, C057) — the row's <c>ck_sos_events_actor</c> demands one or the
/// other.
/// </param>
public sealed record RaiseSosCommand(
    Guid? UserId, string Role, Guid? RideId, double Lat, double Lng, string Source, string? ShareToken);

/// <summary>What the caller is told, and what the row now holds.</summary>
public sealed record RaisedSos(SosEvent Event, bool Dispatched);

/// <summary>
/// D-33: button tap to SMS dispatched, p99 ≤ 5 s, through both gateways at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is: record, announce, dispatch.</b> The row and its <c>sos.raised</c> outbox event
/// commit first, so an operator sees the alert whether or not any gateway takes it — the case where
/// a human being is most needed is exactly the one a "send first, record if it worked" ordering
/// would drop. The dispatch is another service's transaction and cannot be rolled back with ours, so
/// it runs after the commit and its outcome is a second, unguarded write.
/// </para>
/// <para>
/// <b>The five seconds are measured, not asserted.</b> <c>ts</c> is when the button was pressed and
/// <c>dispatched_at</c> is when a gateway took it; the interval between them is the SLO and it is on
/// the row, so it survives the request and can be queried after an incident rather than reconstructed
/// from logs.
/// </para>
/// <para>
/// <b>AL-13 is a lookup, not a join.</b> The emergency contact is read from the two denormalised
/// columns on <c>iam.users</c> that iam-svc maintains inside every mutation of
/// <c>iam.emergency_contacts</c> — its own CLAUDE.md says the reason is this budget.
/// </para>
/// </remarks>
public interface ISosService
{
    Task<RaisedSos> RaiseAsync(RaiseSosCommand command, CancellationToken cancellationToken);

    /// <summary>Own history only; the admin console reads the same events through the live feed.</summary>
    Task<IReadOnlyList<SosEvent>> HistoryAsync(
        Guid callerId, Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISosService"/>
internal sealed class SosService(
    IUnitOfWorkFactory unitOfWorkFactory,
    ISosRepository sosEvents,
    IOutboxWriter outbox,
    INotificationClient notifications,
    ITripShareService shares,
    IOptions<SafetyOptions> options,
    TimeProvider clock,
    ILogger<SosService> logger) : ISosService
{
    private readonly SafetyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<RaisedSos> RaiseAsync(RaiseSosCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var contact = command.UserId is { } raiser
            ? await sosEvents.FindEmergencyContactAsync(raiser, cancellationToken)
            : null;

        // D3': `400 no-emergency-contact`. Checked before anything is written, so a user who cannot
        // be helped by an SMS is told immediately rather than after a row exists — and the app can
        // put them on the "add a contact" screen while the alert still matters.
        if (_options.RequireEmergencyContact && command.UserId is not null && contact?.CanBeReached != true)
        {
            throw new MageRideException(
                MageRideErrors.NoEmergencyContact,
                "Add an emergency contact before using SOS — there is nobody for the alert to reach (AL-13).");
        }

        SosEvent raised;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            raised = await sosEvents.CreateAsync(
                unitOfWork,
                new NewSosEvent(
                    command.UserId,
                    command.Role,
                    command.RideId,
                    command.Lat,
                    command.Lng,
                    contact?.Phone,
                    command.Source,
                    command.ShareToken),
                cancellationToken);

            // R-13: the admin live feed commits with the event. An alert an operator never sees is
            // worse than one that was never SMSed, because the SMS has a retry and the operator
            // does not.
            await outbox.WriteAsync(
                unitOfWork, SafetyEvents.SosRaised(raised, contact?.Name), cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        logger.LogWarning(
            "SOS {SosId} raised by {Role} {UserId} at ({Lat},{Lng}) on ride {RideId} (source {Source}).",
            raised.Id, command.Role, command.UserId, command.Lat, command.Lng, command.RideId, command.Source);

        var dispatched = await DispatchAsync(raised, contact, cancellationToken);

        return new RaisedSos(dispatched, dispatched.SmsStatus == SosSmsStatuses.Dispatched);
    }

    public async Task<IReadOnlyList<SosEvent>> HistoryAsync(
        Guid callerId, Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        // `GET /v1/sos/{userId}/history` names the subject in the path and the contract says "own
        // history only". Anything else would make the route an oracle for whether a given account
        // has ever raised an alert.
        if (callerId != userId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "An SOS history is readable only by its own subject.");
        }

        return await sosEvents.ListForUserAsync(
            userId, before, Math.Clamp(limit, 1, _options.MaxPageSize), cancellationToken);
    }

    // -----------------------------------------------------------------------------------------

    private async Task<SosEvent> DispatchAsync(
        SosEvent raised, EmergencyContact? contact, CancellationToken cancellationToken)
    {
        if (contact?.CanBeReached != true)
        {
            // Reachable only with RequireEmergencyContact off, and then this is the honest record:
            // the alert exists, the console has it, and no message went anywhere.
            await sosEvents.MarkDispatchedAsync(
                raised.Id, SosSmsStatuses.NoContact, null, null, null, cancellationToken);

            logger.LogError(
                "SOS {SosId} has no emergency contact to reach; the admin live feed is the only channel it took.",
                raised.Id);

            return raised with { SmsStatus = SosSmsStatuses.NoContact };
        }

        // A live view of where the alert came from, for whoever receives the SMS.
        //
        // **There is always a link.** An SOS on a ride shares that ride; an SOS raised with no ride
        // — walking to the car, waiting at the kerb — has no trip to track and still has a position,
        // because the contract requires one. A `geo:` URI is what that becomes: every phone opens it
        // in its own map app, it needs no platform surface, and it needs no network to be useful.
        // The alternative was an empty {{link}}, which the renderer correctly refuses — and refusing
        // to render is refusing to send, which is not an acceptable outcome for a panic button.
        var link = await shares.TryMintSosLinkAsync(raised, cancellationToken)
                   ?? GeoUri(raised.Lat, raised.Lng);

        var result = await notifications.SendSosAsync(
            contact.Phone!, contact.RaiserName ?? "A MageRide user", link, cancellationToken);

        var now = clock.GetUtcNow();
        var status = result.Dispatched ? SosSmsStatuses.Dispatched : SosSmsStatuses.Failed;

        // Which transports were tried, one per column — D-33 hands the message to both at once and
        // resolves on whichever answers, so "tried" and "delivered" are different facts and the row
        // keeps both.
        var primary = Describe(result, index: 0);
        var secondary = Describe(result, index: 1);

        await sosEvents.MarkDispatchedAsync(
            raised.Id, status, primary, secondary, result.Dispatched ? now : null, cancellationToken);

        if (result.Dispatched)
        {
            logger.LogInformation(
                "SOS {SosId} dispatched in {ElapsedMs} ms through {Gateways} (delivered by {Provider}).",
                raised.Id,
                (now - raised.Ts).TotalMilliseconds,
                string.Join("+", result.Gateways),
                result.Provider);
        }
        else
        {
            logger.LogError(
                "SOS {SosId} reached no gateway ({Error}). The alert is on the admin live feed and nowhere else.",
                raised.Id, result.Error);
        }

        return raised with
        {
            SmsStatus = status,
            PrimaryGateway = primary,
            SecondaryGateway = secondary,
            DispatchedAt = result.Dispatched ? now : null,
        };
    }

    /// <summary>RFC 5870, which every mobile platform hands to its default map application.</summary>
    private static string GeoUri(double lat, double lng) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"geo:{lat},{lng}");

    /// <summary>
    /// One gateway column: which transport, and whether it was the one that answered.
    /// </summary>
    /// <remarks>
    /// Free text, as C005 left it and 0905 kept it: D6' §7.3 names two gateway families and a
    /// deployment may swap either, so constraining the values would make a configuration change a
    /// migration.
    /// </remarks>
    private static string? Describe(SosDispatchResult result, int index)
    {
        if (result.Gateways.Count <= index)
        {
            return null;
        }

        var gateway = result.Gateways[index];

        return string.Equals(gateway, result.Provider, StringComparison.Ordinal)
            ? $"{gateway}:delivered"
            : $"{gateway}:attempted";
    }
}
