using System.Net;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Provisioning.Trackers;

/// <summary><c>POST /v1/trackers/bind</c> (T-02, US-3.1).</summary>
/// <param name="ActorId">The authenticated caller. Ownership is checked against this, never
/// against anything in the body.</param>
/// <param name="IsAdmin">Whether the caller may act outside their own vehicles.</param>
public sealed record BindTrackerCommand(
    Guid ActorId,
    bool IsAdmin,
    string? Imei,
    string? VehicleId,
    string? Method,
    string? BindCode,
    string? CredentialType,
    IPAddress? RemoteAddress);

/// <summary>A binding and, once, the credential minted for it.</summary>
public sealed record BoundTracker(TrackerBinding Binding, DeviceCredential Credential);

/// <summary>What <c>GET /v1/trackers/{imei}</c> answers with (US-3.12).</summary>
public sealed record TrackerDetail(TrackerBinding Binding, int? BatteryPercent);

/// <summary>What <c>GET /v1/internal/trackers/{imei}/validate</c> answers with (T-01, T-03).</summary>
/// <param name="State">Absent when the IMEI is unknown. <c>valid: false</c> covers unknown,
/// quarantined and revoked alike — the adapter closes the socket in every case.</param>
public sealed record ValidationVerdict(bool Valid, Guid? VehicleId, string? State);

/// <summary>
/// The tracker credential lifecycle: mint, bind, rotate, revoke (T-02, T-03, T-08, T-12).
/// </summary>
public interface ITrackerService
{
    /// <summary>
    /// Binds an IMEI to a vehicle and mints its credential.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>BindAsync</c>.</b> Minimal APIs treat any parameter type carrying a method of
    /// that name as custom-bound, so a handler taking <see cref="ITrackerService"/> as a
    /// dependency would fail to build the route table at start-up. Same reason
    /// <c>IMerchantService.BindMerchantAsync</c> is spelled the way it is (C028).
    /// </remarks>
    Task<BoundTracker> BindTrackerAsync(BindTrackerCommand command, CancellationToken cancellationToken);

    /// <summary>Releases a binding so the tracker can be bound elsewhere. Revokes its credentials.</summary>
    Task UnbindAsync(Guid actorId, bool isAdmin, string? imei, CancellationToken cancellationToken);

    /// <summary>US-3.8, T-12 — admin decommission. Same terminal state, different authority and reason.</summary>
    Task DecommissionAsync(Guid actorId, string? imei, CancellationToken cancellationToken);

    Task<TrackerDetail> GetAsync(Guid actorId, bool isAdmin, string? imei, CancellationToken cancellationToken);

    /// <summary>US-3.6 — chooses the single publisher for this vehicle.</summary>
    Task<TrackerBinding> SwitchSourceAsync(
        Guid actorId, bool isAdmin, string? imei, string? source, CancellationToken cancellationToken);

    /// <summary>US-3.5 — mints a replacement while the outgoing credential is still valid (T-02).</summary>
    Task<DeviceCredential> RotateAsync(string? imei, CancellationToken cancellationToken);

    /// <summary>The adapter's per-connect resolution (T-01/T-03) and the T-12 credential check.</summary>
    Task<ValidationVerdict> ValidateAsync(
        string? imei, string? credentialSerial, IPAddress? remoteAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Holds a binding because something outside this service saw one IMEI on two devices (T-08).
    /// </summary>
    /// <remarks>
    /// The other half of the anti-clone rule. D6' §4.3's "two devices presenting the same IMEI
    /// within 24 h" is decidable here only at <c>bind</c>, where two claims arrive with two
    /// identities; at the adapter a clone presents a *copy* of the genuine credential, so what
    /// distinguishes it is two live sockets holding one identity — state the adapter has and this
    /// service does not. So the adapter reports and this service adjudicates: it checks the
    /// sighting trail, holds the binding, puts the credential on certificate-hold and raises the
    /// US-3.4 alert.
    /// </remarks>
    /// <returns>The held binding, or <see langword="null"/> when there was no ACTIVE one to hold.</returns>
    Task<TrackerBinding?> QuarantineAsync(
        string? imei, string? reportedBy, string? detail, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerService"/>
public sealed class TrackerService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    ITrackerBindingRepository bindings,
    IDeviceCertificateRepository certificates,
    IImeiSightingRepository sightings,
    IVehicleLookupRepository vehicles,
    ICertificateAuthority authority,
    ITrackerCache cache,
    IOutboxWriter outbox,
    IOptions<ProvisioningOptions> options,
    TimeProvider clock,
    ILogger<TrackerService> logger) : ITrackerService
{
    /// <summary>Postgres' unique-violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// The cell voltage window a GT06/JT808-class tracker reports across, in millivolts.
    /// </summary>
    /// <remarks>
    /// D3' types <c>battery</c> as a percentage 0–100 and <c>prov.tracker_bindings</c> stores
    /// <c>battery_mv</c>; they are different quantities and something has to convert. This is a
    /// linear map over a single-cell Li-ion's usable range, which is what the device families in
    /// D6' §4.1 carry. It is an approximation and is only ever shown to a human deciding whether
    /// to go and charge something.
    /// </remarks>
    private const int BatteryEmptyMv = 3_300;
    private const int BatteryFullMv = 4_200;

    private readonly ProvisioningOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<BoundTracker> BindTrackerAsync(BindTrackerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = Validate(command);

        try
        {
            return await BindOnceAsync(command, request, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // ux_tracker_imei_active rejected the insert, so another request bound this IMEI
            // between our read and our write. That is not an error to report — it is *exactly* the
            // T-08 signal, two devices claiming one IMEI at once. Re-running now finds the winner's
            // ACTIVE row and takes the anti-clone path deliberately rather than by accident.
            logger.LogWarning(
                "Concurrent bind of IMEI {Imei}; re-running so the anti-clone rule sees the committed binding",
                request.Imei);

            return await BindOnceAsync(command, request, cancellationToken);
        }
    }

    public Task UnbindAsync(Guid actorId, bool isAdmin, string? imei, CancellationToken cancellationToken) =>
        ReleaseAsync(
            actorId,
            isAdmin,
            imei,
            BindingStateReasons.Unbound,
            RevocationReasons.CessationOfOperation,
            requireAdmin: false,
            cancellationToken);

    public Task DecommissionAsync(Guid actorId, string? imei, CancellationToken cancellationToken) =>
        ReleaseAsync(
            actorId,
            isAdmin: true,
            imei,
            BindingStateReasons.Decommissioned,
            RevocationReasons.CessationOfOperation,
            requireAdmin: true,
            cancellationToken);

    public async Task<TrackerDetail> GetAsync(
        Guid actorId, bool isAdmin, string? imei, CancellationToken cancellationToken)
    {
        var value = Imeis.RequirePath(imei);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var binding = await bindings.FindLatestByImeiAsync(connection, null, value, cancellationToken)
                      ?? throw NoTracker(value);

        await RequireVehicleAccessAsync(connection, null, actorId, isAdmin, binding.VehicleId, cancellationToken);

        return new TrackerDetail(binding, ToBatteryPercent(binding.BatteryMv));
    }

    public async Task<TrackerBinding> SwitchSourceAsync(
        Guid actorId, bool isAdmin, string? imei, string? source, CancellationToken cancellationToken)
    {
        var value = Imeis.RequirePath(imei);

        if (!PublisherSources.IsKnown(source))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["source"] = ["source must be 'mobile' or 'hardware'."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var binding = await bindings.FindActiveByImeiAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, value, cancellationToken)
                      ?? throw NoTracker(value);

        await RequireVehicleAccessAsync(
            unitOfWork.Connection, unitOfWork.Transaction, actorId, isAdmin, binding.VehicleId, cancellationToken);

        var updated = await bindings.UpdateSourceAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, binding.Id, source!, cancellationToken)
                      ?? throw NoTracker(value);

        // US-3.6 is "exactly one publisher at a time", and the two planes that enforce it —
        // trip-state's session writer and the position processor — learn which one from here.
        await outbox.WriteAsync(unitOfWork, TrackerEvents.SourceSwitched(updated), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} publishes from {Source} (IMEI {Imei})", updated.VehicleId, source, value);

        return updated;
    }

    public async Task<DeviceCredential> RotateAsync(string? imei, CancellationToken cancellationToken)
    {
        var value = Imeis.RequirePath(imei);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var binding = await bindings.FindActiveByImeiAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, value, cancellationToken)
                      ?? throw NoTracker(value);

        var credential = await RotateBindingAsync(unitOfWork, binding, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return credential;
    }

    /// <summary>
    /// Mints a replacement credential on the caller's transaction, leaving the outgoing one valid.
    /// </summary>
    /// <remarks>
    /// Shared by <c>POST /v1/internal/trackers/{imei}/rotate</c> and the rotation sweep, so a cron
    /// pass and an operator's manual rotation cannot drift apart. <b>The old credential is not
    /// revoked here.</b> Revoking on rotation would brick every tracker that happened to be out of
    /// coverage — see <see cref="DevicePkiOptions.RotationLeadTime"/>.
    /// </remarks>
    internal async Task<DeviceCredential> RotateBindingAsync(
        IUnitOfWork unitOfWork, TrackerBinding binding, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var credential = authority.Issue(binding.CredentialType, binding.VehicleId, binding.Imei, now);

        var updated = await bindings.UpdateCredentialAsync(
                          unitOfWork.Connection,
                          unitOfWork.Transaction,
                          binding.Id,
                          credential.Serial,
                          credential.RotatesAt,
                          cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict,
                          $"Binding {binding.Id} left ACTIVE while its credential was being rotated.");

        await certificates.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            binding.Id,
            credential.Serial,
            credential.Type,
            credential.MaterialHash,
            credential.IssuedAt,
            credential.ExpiresAt,
            cancellationToken);

        await outbox.WriteAsync(
            unitOfWork,
            TrackerEvents.CredentialRotated(updated, binding.CredentialSerial, credential.Serial, now),
            cancellationToken);

        logger.LogInformation(
            "Rotated {Imei} from {PreviousSerial} to {Serial}; the outgoing credential stays valid until it expires",
            binding.Imei,
            binding.CredentialSerial,
            credential.Serial);

        return credential;
    }

    public async Task<ValidationVerdict> ValidateAsync(
        string? imei, string? credentialSerial, IPAddress? remoteAddress, CancellationToken cancellationToken)
    {
        // Unknown rather than malformed: this is the adapter's hot path and every negative answer
        // is the same instruction — close the socket.
        if (!Imeis.IsValid(imei))
        {
            return new ValidationVerdict(false, null, null);
        }

        // The fast path, and the only one a well-behaved fleet ever takes: no credential to
        // reconcile, so the cached vehicle id is the whole answer and Postgres is not touched
        // (T-03). An adapter that does report a serial pays a database round trip, because the
        // anti-clone rule cannot be evaluated against a cache that holds one value per IMEI.
        if (string.IsNullOrWhiteSpace(credentialSerial))
        {
            if (await cache.ResolveAsync(imei, cancellationToken) is { } cached)
            {
                return new ValidationVerdict(true, cached, BindingStates.Active);
            }
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var binding = await bindings.FindLatestByImeiAsync(
            unitOfWork.Connection, unitOfWork.Transaction, imei, cancellationToken);

        if (binding is null)
        {
            // Still recorded. An IMEI nobody bound turning up at an adapter is what a scanner
            // looks like, and the sighting is the only trace of it.
            await sightings.RecordAsync(
                unitOfWork.Connection, unitOfWork.Transaction, imei, credentialSerial,
                SightingSources.Validate, null, remoteAddress, now, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);

            return new ValidationVerdict(false, null, null);
        }

        if (!binding.IsActive)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            // No sighting for a device whose binding is already held: the rule it would feed has
            // fired, and recording more presentations only grows the table.
            return new ValidationVerdict(false, null, binding.State);
        }

        await sightings.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, imei, credentialSerial,
            SightingSources.Validate, null, remoteAddress, now, cancellationToken);

        // The credential has to be one this binding currently holds: issued to it, not revoked and
        // not expired. This is where T-12 lands on the TCP path — a revoked serial stops
        // authenticating here whether or not the adapter ever saw the Redis message.
        //
        // Deliberately *not* a clone check. A rotation leaves two presentable serials on one
        // binding for the overlap window (DevicePkiOptions.RotationLeadTime), so "this IMEI showed
        // two serials" is the normal state of every device being renewed — and a real clone copies
        // the certificate, so it presents the *same* serial anyway. Telling two sockets holding one
        // credential apart needs the sockets, which the adapter has and this service does not; it
        // reports what it sees through POST /v1/internal/trackers/{imei}/quarantine, and the
        // sighting rows above are the evidence that call is judged against.
        if (credentialSerial is { Length: > 0 }
            && !await certificates.IsPresentableAsync(
                unitOfWork.Connection, unitOfWork.Transaction, binding.Id, credentialSerial, now, cancellationToken))
        {
            await unitOfWork.CommitAsync(cancellationToken);
            return new ValidationVerdict(false, null, binding.State);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        // Re-primes on the way back, so a cache flush costs one Postgres read per device rather
        // than one per connect for as long as the device stays online.
        await cache.PrimeAsync(imei, binding.VehicleId, cancellationToken);

        return new ValidationVerdict(true, binding.VehicleId, binding.State);
    }

    public async Task<TrackerBinding?> QuarantineAsync(
        string? imei, string? reportedBy, string? detail, CancellationToken cancellationToken)
    {
        var value = Imeis.RequirePath(imei);
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var binding = await bindings.FindActiveByImeiAsync(
            unitOfWork.Connection, unitOfWork.Transaction, value, cancellationToken);

        if (binding is null)
        {
            // Already held, already revoked, or never bound. Idempotent on purpose: an adapter
            // that reports the same clone on every reconnect must not accumulate alerts, and one
            // reporting an IMEI nobody bound has told us something the sighting trail already has.
            await unitOfWork.RollbackAsync(cancellationToken);
            return null;
        }

        var competing = await sightings.ListOtherSerialsAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            value,
            excludingSerial: null,
            now - _options.AntiCloneWindow,
            cancellationToken);

        await QuarantineAsync(
            unitOfWork,
            binding,
            challenger: null,
            competing,
            $"{reportedBy ?? "an adapter"} reported IMEI {value} on more than one device: " +
            $"{detail ?? "no detail given"}.",
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceReleaseAsync(binding, [], BindingStateReasons.ImeiDuplicate, cancellationToken);

        return binding;
    }

    private async Task<BoundTracker> BindOnceAsync(
        BindTrackerCommand command, BindRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await vehicles.FindAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, request.VehicleId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"No vehicle {request.VehicleId}.");

        await RequireVehicleAccessAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.ActorId, command.IsAdmin, vehicle, cancellationToken);

        await sightings.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, request.Imei, null,
            SightingSources.Bind, command.ActorId, command.RemoteAddress, now, cancellationToken);

        var incumbent = await bindings.FindActiveByImeiAsync(
            unitOfWork.Connection, unitOfWork.Transaction, request.Imei, cancellationToken);

        if (incumbent is not null)
        {
            var quarantined = await ResolveIncumbentAsync(unitOfWork, incumbent, request, now, cancellationToken);

            if (quarantined)
            {
                // Committed first, then reported. Both records have to be held before the caller
                // is told the bind failed — a 409 that rolled the quarantine back would leave the
                // incumbent publishing and the operator with nothing to escalate (US-3.4).
                await unitOfWork.CommitAsync(cancellationToken);
                await AnnounceReleaseAsync(incumbent, [], BindingStateReasons.ImeiDuplicate, cancellationToken);

                throw new MageRideException(
                    MageRideErrors.ImeiDuplicate,
                    $"IMEI {request.Imei} is already bound to vehicle {incumbent.VehicleId}. Both bindings are " +
                    "quarantined pending admin resolution (T-08).");
            }
        }

        var credential = authority.Issue(request.CredentialType, request.VehicleId, request.Imei, now);

        var binding = await bindings.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            request.Imei,
            request.VehicleId,
            vehicle.FleetId,
            credential.Serial,
            credential.Type,
            BindingStates.Active,
            stateReason: null,
            credential.RotatesAt,
            // A freshly bound tracker is the authoritative publisher for its vehicle (T-11); the
            // driver app is switched back on explicitly through switch-source (US-3.6).
            PublisherSources.Hardware,
            cancellationToken);

        await certificates.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            binding.Id,
            credential.Serial,
            credential.Type,
            credential.MaterialHash,
            credential.IssuedAt,
            credential.ExpiresAt,
            cancellationToken);

        await outbox.WriteAsync(unitOfWork, TrackerEvents.TrackerBound(binding), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        // After COMMIT and best effort: an unreachable Redis costs the adapter a Postgres lookup
        // on the next connect, not the binding.
        await cache.PrimeAsync(binding.Imei, binding.VehicleId, cancellationToken);
        await cache.PublishAsync(
            new TrackerCredentialSignal(
                TrackerEventTypes.TrackerBound, binding.Imei, binding.VehicleId, [], null, now),
            cancellationToken);

        logger.LogInformation(
            "Bound IMEI {Imei} to vehicle {VehicleId} with a {CredentialType} credential, serial {Serial}",
            binding.Imei,
            binding.VehicleId,
            binding.CredentialType,
            binding.CredentialSerial);

        return new BoundTracker(binding, credential);
    }

    /// <summary>
    /// Decides what a second bind of a live IMEI means, and does it.
    /// </summary>
    /// <returns><see langword="true"/> when both records were quarantined and the bind must fail.</returns>
    /// <remarks>
    /// D6' §4.3 quarantines "two devices presenting the same IMEI <b>within 24 h</b>". Inside the
    /// window the incumbent and the challenger are both held and nobody publishes — an IMEI is
    /// globally unique by construction, so a second claim on a live one is either a clone or a
    /// mis-keyed provisioning, and both are things a human has to look at. Outside it, the
    /// incumbent is treated as stale and superseded: an operator moving a tracker to another
    /// vehicle a week later has cloned nothing, and holding both would make them wait for an admin
    /// to undo a legitimate re-provision.
    /// </remarks>
    private async Task<bool> ResolveIncumbentAsync(
        IUnitOfWork unitOfWork, TrackerBinding incumbent, BindRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lastPresented = Latest(incumbent.CreatedAt, incumbent.StateChangedAt, incumbent.LastSeenAt);

        if (now - lastPresented > _options.AntiCloneWindow)
        {
            await ReleaseBindingAsync(
                unitOfWork,
                incumbent,
                BindingStateReasons.Superseded,
                RevocationReasons.Superseded,
                cancellationToken);

            logger.LogInformation(
                "IMEI {Imei} was last live at {LastPresented}, outside the {Window:g} anti-clone window; " +
                "the previous binding to vehicle {VehicleId} is superseded rather than quarantined",
                incumbent.Imei,
                lastPresented,
                _options.AntiCloneWindow,
                incumbent.VehicleId);

            return false;
        }

        // The challenger is materialised as a QUARANTINED binding rather than being refused
        // outright, because D6' §4.3 holds *both* and US-3.4's queue has to show an operator two
        // rows to choose between. Its credential is minted and revoked on hold in the same
        // transaction: prov.tracker_bindings.credential_serial is NOT NULL and a binding pointing
        // at a serial no certificate row carries would be a dangling reference in an audit trail.
        var challengerCredential = authority.Issue(request.CredentialType, request.VehicleId, request.Imei, now);

        var challenger = await bindings.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            request.Imei,
            request.VehicleId,
            null,
            challengerCredential.Serial,
            challengerCredential.Type,
            BindingStates.Quarantined,
            BindingStateReasons.ImeiDuplicate,
            challengerCredential.RotatesAt,
            source: null,
            cancellationToken);

        await certificates.InsertAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            challenger.Id,
            challengerCredential.Serial,
            challengerCredential.Type,
            challengerCredential.MaterialHash,
            challengerCredential.IssuedAt,
            challengerCredential.ExpiresAt,
            cancellationToken);

        await certificates.RevokeForBindingAsync(
            unitOfWork.Connection, unitOfWork.Transaction, challenger.Id,
            RevocationReasons.CertificateHold, now, cancellationToken);

        await QuarantineAsync(
            unitOfWork,
            incumbent,
            challenger,
            [incumbent.CredentialSerial, challenger.CredentialSerial],
            $"Vehicle {request.VehicleId} claimed IMEI {request.Imei}, already bound to vehicle " +
            $"{incumbent.VehicleId} since {incumbent.CreatedAt:O}.",
            cancellationToken);

        return true;
    }

    /// <summary>Holds the incumbent (and the challenger, when there is one) and raises the alert.</summary>
    private async Task QuarantineAsync(
        IUnitOfWork unitOfWork,
        TrackerBinding incumbent,
        TrackerBinding? challenger,
        IReadOnlyCollection<string> competingSerials,
        string detail,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var held = await bindings.TransitionAsync(
                       unitOfWork.Connection,
                       unitOfWork.Transaction,
                       incumbent.Id,
                       BindingStates.Active,
                       BindingStates.Quarantined,
                       BindingStateReasons.ImeiDuplicate,
                       now,
                       cancellationToken)
                   ?? throw new MageRideException(
                       MageRideErrors.Conflict,
                       $"Binding {incumbent.Id} left ACTIVE while it was being quarantined.");

        // Held, not destroyed: certificate_hold is the one RFC 5280 reason a CA may lift, which is
        // what US-3.4's admin resolution does for whichever device turns out to be genuine.
        await certificates.RevokeForBindingAsync(
            unitOfWork.Connection, unitOfWork.Transaction, held.Id,
            RevocationReasons.CertificateHold, now, cancellationToken);

        List<TrackerBinding> holders = challenger is null ? [held] : [held, challenger];

        await outbox.WriteAsync(
            unitOfWork,
            [
                TrackerEvents.TrackerUnbound(held, BindingStateReasons.ImeiDuplicate),
                TrackerEvents.TrackerQuarantined(held, holders, competingSerials, detail),
            ],
            cancellationToken);

        logger.LogWarning(
            "T-08 quarantine on IMEI {Imei}: {Detail} Both records are held pending admin resolution (US-3.4).",
            held.Imei,
            detail);
    }

    private async Task ReleaseAsync(
        Guid actorId,
        bool isAdmin,
        string? imei,
        string stateReason,
        string revocationReason,
        bool requireAdmin,
        CancellationToken cancellationToken)
    {
        var value = Imeis.RequirePath(imei);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var binding = await bindings.FindActiveByImeiAsync(
            unitOfWork.Connection, unitOfWork.Transaction, value, cancellationToken);

        if (binding is null)
        {
            // 404 rather than 204: unlike a share revoke, "the binding is already gone" is not
            // necessarily what the caller wanted — a decommission that silently succeeded against
            // a *quarantined* record would read as "this device is dealt with" when it is not.
            await unitOfWork.RollbackAsync(cancellationToken);
            throw NoTracker(value);
        }

        if (!requireAdmin)
        {
            await RequireVehicleAccessAsync(
                unitOfWork.Connection, unitOfWork.Transaction, actorId, isAdmin, binding.VehicleId, cancellationToken);
        }

        var serials = await ReleaseBindingAsync(unitOfWork, binding, stateReason, revocationReason, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
        await AnnounceReleaseAsync(binding, serials, stateReason, cancellationToken);

        logger.LogInformation(
            "IMEI {Imei} released from vehicle {VehicleId} ({Reason}); {Count} credential(s) revoked",
            binding.Imei,
            binding.VehicleId,
            stateReason,
            serials.Count);
    }

    /// <summary>
    /// Moves a binding to REVOKED, revokes every credential on it and queues both events, all on
    /// the caller's transaction.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReleaseBindingAsync(
        IUnitOfWork unitOfWork,
        TrackerBinding binding,
        string stateReason,
        string revocationReason,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var revoked = await bindings.TransitionAsync(
                          unitOfWork.Connection,
                          unitOfWork.Transaction,
                          binding.Id,
                          BindingStates.Active,
                          BindingStates.Revoked,
                          stateReason,
                          now,
                          cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict,
                          $"Binding {binding.Id} left ACTIVE while it was being revoked.");

        var serials = await certificates.RevokeForBindingAsync(
            unitOfWork.Connection, unitOfWork.Transaction, revoked.Id, revocationReason, now, cancellationToken);

        // Both events, in one transaction with the state change. `tracker.unbound` is the D6' §4.3
        // cache invalidation and `tracker.revoked` is the T-12 credential fact; a consumer needs
        // the first and a broker or adapter needs the second, and neither is derivable from the
        // other.
        await outbox.WriteAsync(
            unitOfWork,
            [
                TrackerEvents.TrackerUnbound(revoked, stateReason),
                TrackerEvents.TrackerRevoked(revoked, serials, revocationReason),
            ],
            cancellationToken);

        return serials;
    }

    /// <summary>The Redis half, after COMMIT — the part that makes revocation sub-second (T-12).</summary>
    private async Task AnnounceReleaseAsync(
        TrackerBinding binding, IReadOnlyList<string> serials, string reason, CancellationToken cancellationToken)
    {
        await cache.InvalidateAsync(binding.Imei, cancellationToken);

        await cache.PublishAsync(
            new TrackerCredentialSignal(
                TrackerEventTypes.TrackerRevoked,
                binding.Imei,
                binding.VehicleId,
                serials,
                reason,
                clock.GetUtcNow()),
            cancellationToken);
    }

    private Task RequireVehicleAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid actorId,
        bool isAdmin,
        Guid vehicleId,
        CancellationToken cancellationToken) =>
        isAdmin
            ? Task.CompletedTask
            : RequireVehicleAccessCoreAsync(connection, transaction, actorId, vehicleId, cancellationToken);

    private async Task RequireVehicleAccessCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid actorId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.FindAsync(connection, transaction, vehicleId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        await RequireVehicleAccessAsync(connection, transaction, actorId, isAdmin: false, vehicle, cancellationToken);
    }

    private async Task RequireVehicleAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid actorId,
        bool isAdmin,
        VehicleReference vehicle,
        CancellationToken cancellationToken)
    {
        if (isAdmin || vehicle.OwnerId == actorId)
        {
            return;
        }

        // A fleet's trackers are provisioned by the Fleet Portal, whose operator does not own the
        // vehicles — the fleet organisation does (AL-03). Owning the vehicle and running the fleet
        // it is rostered to are the two ways in, and there is no third.
        if (vehicle.FleetId is { } fleetId
            && await vehicles.IsFleetPrincipalAsync(connection, transaction, fleetId, actorId, cancellationToken))
        {
            return;
        }

        throw new MageRideException(MageRideErrors.NotOwner, "This vehicle belongs to another operator.");
    }

    private BindRequest Validate(BindTrackerCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Imeis.IsValid(command.Imei))
        {
            errors["imei"] = ["imei must be exactly 15 digits."];
        }

        if (!Guid.TryParse(command.VehicleId, out var vehicleId) || vehicleId == Guid.Empty)
        {
            errors["vehicleId"] = ["vehicleId is required and must be an identifier."];
        }

        if (!BindMethods.IsKnown(command.Method))
        {
            errors["method"] = ["method must be 'manual', 'qr' or 'admin_code'."];
        }

        if (!CredentialTypes.IsKnown(command.CredentialType))
        {
            errors["credentialType"] = ["credentialType must be 'x509' or 'psk'."];
        }

        // D3': "bindCode: string? — Required when method=admin_code". The code itself is checked
        // by whoever issued it; what is enforced here is that a request claiming to carry one
        // actually does, so an admin-code bind cannot be spelled as a code-less one.
        if (command.Method == BindMethods.AdminCode && string.IsNullOrWhiteSpace(command.BindCode))
        {
            errors["bindCode"] = ["bindCode is required when method is 'admin_code'."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return new BindRequest(command.Imei!, vehicleId, command.CredentialType!);
    }

    private static DateTimeOffset Latest(DateTimeOffset first, DateTimeOffset second, DateTimeOffset? third)
    {
        var latest = first > second ? first : second;
        return third is { } value && value > latest ? value : latest;
    }

    /// <summary>Millivolts to the percentage D3' types the field as. Null in, null out.</summary>
    internal static int? ToBatteryPercent(int? batteryMv) => batteryMv is not { } mv
        ? null
        : Math.Clamp((int)Math.Round((mv - BatteryEmptyMv) * 100.0 / (BatteryFullMv - BatteryEmptyMv)), 0, 100);

    private static MageRideException NoTracker(string imei) =>
        new(MageRideErrors.NotFound, $"No tracker bound to IMEI {imei}.");

    private sealed record BindRequest(string Imei, Guid VehicleId, string CredentialType);
}
