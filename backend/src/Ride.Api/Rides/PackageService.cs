using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>The multipart body of <c>POST /v1/rides/{rideId}/package/proof-photo</c> (P-10).</summary>
/// <param name="CapturedGeo">
/// Where the phone said the photo was taken. Δ C037 on the contract's multipart schema, which
/// declares only <c>file</c> and <c>note</c> — <c>rides.proof_artifacts.captured_geo</c> exists
/// (migration 0607) and D5' §11 names it as part of the proof, so leaving it unfillable would make
/// the column permanently NULL and the evidence weaker than the schema promises.
/// </param>
/// <param name="Note">
/// <b>Accepted and dropped.</b> <c>rides.proof_artifacts</c> has no note column in either DDL
/// source, and a note stored nowhere is worse than a note not taken.
/// </param>
public sealed record ProofPhotoCommand(
    Guid DriverId,
    Guid RideId,
    string? FileName,
    long? Length,
    Stream Content,
    GeoPoint? CapturedGeo,
    string? Note);

/// <summary>What the proof-photo route answers with, plus where the ride landed.</summary>
public sealed record PackageProof(Guid ArtifactId, RideRow Ride);

/// <summary>
/// The two OTP gates, the photo-proof fallback and the cash-on-delivery confirmation
/// (P-07, P-08, P-10, P-14; ADD §11.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>No new states.</b> ADD Appendix B.2 invariant 6 makes the machine kind-agnostic and this
/// component keeps it that way: <c>package.picked_up</c> co-fires with the ordinary
/// <c>Accepted|DriverArrived → InProgress</c> and <c>package.delivered</c> with
/// <c>InProgress → Completed → PaymentPending</c>. The gates decide <em>whether</em> the ride may
/// move, never <em>where</em> to.
/// </para>
/// <para>
/// <b>Both events, every time.</b> Each gate writes the <c>ride.*</c> state snapshot the aggregate
/// owes its consumers <em>and</em> the <c>package.*</c> domain event ADD §11.16 names. Spelling a
/// package's completion only as <c>package.delivered</c> would leave dispatch-svc — which releases
/// the driver on <c>ride.completed</c> — holding a driver who is not on a ride.
/// </para>
/// </remarks>
public interface IPackageService
{
    /// <summary>P-07's pickup gate: the sender reads out four digits and the parcel changes hands.</summary>
    Task<RideRow> VerifyPickupOtpAsync(Guid driverId, Guid rideId, string? otp, CancellationToken cancellationToken);

    /// <summary>P-07's delivery gate: the recipient's code completes the delivery.</summary>
    Task<RideRow> VerifyDeliveryOtpAsync(Guid driverId, Guid rideId, string? otp, CancellationToken cancellationToken);

    /// <summary>P-10: nobody was there, so a photograph is the proof instead.</summary>
    Task<PackageProof> UploadProofPhotoAsync(ProofPhotoCommand command, CancellationToken cancellationToken);

    /// <summary>P-08: the driver has the cash. The ride reaches its terminal money state.</summary>
    Task<RideRow> ConfirmCashOnDeliveryAsync(
        Guid driverId, Guid rideId, long? collectedMinor, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPackageService"/>
public sealed class PackageService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connectionFactory,
    IRideRepository rides,
    IProofArtifactRepository artifacts,
    IProofPhotoStore photos,
    RideStateWriter stateWriter,
    IOutboxWriter outbox,
    PackageOtpCodec otps,
    IOptions<RideOptions> options,
    TimeProvider timeProvider,
    ILogger<PackageService> logger) : IPackageService
{
    /// <summary>ADD §11.16: the pickup gate is legal from either state the driver may be sitting in.</summary>
    private static readonly string[] PickupOrigins = [RideStates.Accepted, RideStates.DriverArrived];

    private static readonly string[] DeliveryOrigins = [RideStates.InProgress];

    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<RideRow> VerifyPickupOtpAsync(
        Guid driverId, Guid rideId, string? otp, CancellationToken cancellationToken)
    {
        RequireWellFormed(otp);

        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await RequirePackageAsync(unitOfWork, rideId, driverId, cancellationToken);

        // Minted here rather than read back, because the digest booking wrote is all this service
        // kept. D5' §11 says the delivery code is generated at ride creation and that its plaintext
        // leaves the server exactly once — but ADD §11.16 sends it to the recipient at *pickup*,
        // and a plaintext nobody stored cannot be sent an hour later. So a code exists from
        // creation (ck_rides_package_complete is satisfied at INSERT) and the code that is actually
        // sent is minted at the moment of sending, replacing its digest in the same statement that
        // takes the pickup gate. It exists in the clear for one hop instead of for the whole
        // booking, which is strictly the better half of the trade. Raised in the C037 handoff.
        var deliveryOtp = otps.Generate();
        var rotated = otps.Hash(ride.PassengerId, ride.ClientRequestId, PackageOtpPurpose.Delivery, deliveryOtp);

        var hash = otps.Hash(ride.PassengerId, ride.ClientRequestId, PackageOtpPurpose.Pickup, otp!);

        // One conditional UPDATE per legal origin, so the audit row records which of the two the
        // driver was actually in — the same reason RideService.AdvanceAsDriverAsync does it.
        RideRow? picked = null;
        string? fromState = null;

        foreach (var origin in PickupOrigins)
        {
            picked = await rides.ConsumePackageOtpAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                rideId,
                driverId,
                PackageOtpPurpose.Pickup,
                hash,
                [origin],
                RideStates.InProgress,
                _options.MaxOtpAttempts,
                rotated,
                cancellationToken);

            if (picked is not null)
            {
                fromState = origin;
                break;
            }
        }

        if (picked is null)
        {
            throw await ChargeAndDiagnoseAsync(
                unitOfWork, ride, driverId, PackageOtpPurpose.Pickup, hash, PickupOrigins, now, cancellationToken);
        }

        await stateWriter.RecordAsync(
            unitOfWork,
            picked,
            fromState,
            RideTransitions.Actors.Driver,
            driverId,
            RideReasonCodes.PackagePickedUp,
            [
                RideEvents.Build(RideEventTypes.Started, picked, Guid.NewGuid(), now),

                // The delivery code rides out on this event and on nothing else. AL-21's branch is
                // notification-svc's: a registered recipient gets an FCM deep link, an unregistered
                // one an SMS carrying a safety.trip_share_tokens link to the no-login tracking page.
                RideEvents.BuildPackage(
                    picked, RideEventTypes.PackagePickedUp, Payload(picked, deliveryOtp: deliveryOtp),
                    Guid.NewGuid(), now),
            ],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Package {RideId} picked up by driver {DriverId}; the recipient's code has been issued", rideId, driverId);

        return picked;
    }

    public async Task<RideRow> VerifyDeliveryOtpAsync(
        Guid driverId, Guid rideId, string? otp, CancellationToken cancellationToken)
    {
        RequireWellFormed(otp);

        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await RequirePackageAsync(unitOfWork, rideId, driverId, cancellationToken);
        var hash = otps.Hash(ride.PassengerId, ride.ClientRequestId, PackageOtpPurpose.Delivery, otp!);

        var delivered = await rides.ConsumePackageOtpAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            rideId,
            driverId,
            PackageOtpPurpose.Delivery,
            hash,
            DeliveryOrigins,
            RideStates.Completed,
            _options.MaxOtpAttempts,
            rotatedDeliveryOtpHash: null,
            cancellationToken);

        if (delivered is null)
        {
            throw await ChargeAndDiagnoseAsync(
                unitOfWork, ride, driverId, PackageOtpPurpose.Delivery, hash, DeliveryOrigins, now, cancellationToken);
        }

        var pending = await HandOffToFareAsync(
            unitOfWork, delivered, RideReasonCodes.PackageDeliveredByOtp, proofArtifactId: null, now, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Package {RideId} delivered against the recipient's OTP", rideId);

        return pending;
    }

    public async Task<PackageProof> UploadProofPhotoAsync(
        ProofPhotoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ProofPhotoUpload.RequireWithinLimit(command.Length, _options.ProofPhotoMaxBytes);

        var now = timeProvider.GetUtcNow();

        // Checked before a byte is written: a photo stored against a ride this driver does not hold,
        // or one that is not out for delivery, is evidence of nothing.
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            var current = await rides.FindAsync(connection, null, command.RideId, cancellationToken);

            RequireDeliverable(current, command.RideId, command.DriverId);
        }

        var artifactId = Guid.NewGuid();
        var stored = await photos.SaveAsync(
            command.RideId, artifactId, command.FileName, command.Content, cancellationToken);

        // The write happens before the transaction, so a ride that moved in between leaves an
        // unreferenced file rather than a completion with no proof behind it. That is the right way
        // round: the file is recoverable evidence and the missing artifact row would not be.
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var delivered = await rides.AdvanceAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            command.RideId,
            DeliveryOrigins,
            RideStates.Completed,
            expectedVersion: null,
            requiredDriverId: command.DriverId,
            cancellationToken);

        if (delivered is null)
        {
            var current = await rides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, command.RideId, cancellationToken);

            await unitOfWork.RollbackAsync(cancellationToken);

            logger.LogWarning(
                "Delivery photo {ArtifactId} for ride {RideId} was stored at {StorageUrl} but the ride moved on; " +
                "no artifact row was written",
                artifactId, command.RideId, stored.StorageUrl);

            RequireDeliverable(current, command.RideId, command.DriverId);

            throw new MageRideException(
                MageRideErrors.Conflict, "The ride moved while the photo was being stored.");
        }

        await artifacts.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            new NewProofArtifact(
                Id: artifactId,
                RideId: command.RideId,
                Kind: ProofArtifactKinds.DeliveryPhoto,
                StorageUrl: stored.StorageUrl,
                Sha256: stored.Sha256,
                CapturedGeo: command.CapturedGeo),
            cancellationToken);

        var pending = await HandOffToFareAsync(
            unitOfWork, delivered, RideReasonCodes.PackageDeliveredByPhoto, artifactId, now, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Package {RideId} delivered on photo proof {ArtifactId} ({Bytes} bytes); the recipient was absent (P-10)",
            command.RideId, artifactId, stored.Bytes);

        return new PackageProof(artifactId, pending);
    }

    public async Task<RideRow> ConfirmCashOnDeliveryAsync(
        Guid driverId, Guid rideId, long? collectedMinor, CancellationToken cancellationToken)
    {
        if (collectedMinor is null or < 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount, "collectedMinor is required and must not be negative.");
        }

        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

        if (ride is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw RideProblems.NotFound(rideId);
        }

        if (ride.AcceptedDriverId != driverId)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "Only the driver who delivered the package can bank its cash.");
        }

        if (!ride.IsCashOnDelivery)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.PaymentMethodInvalid,
                "This ride was not booked cash-on-delivery; its fare settles through fare-svc (R-05).");
        }

        if (RideStates.IsTerminal(ride.State))
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            // Including a redelivery of this very command: the ride is already
            // CashOnDeliveryCollected and a second confirmation must not post a second earning.
            throw new MageRideException(
                MageRideErrors.PaymentAlreadySettled, $"This ride is already {ride.State}.");
        }

        // R-05 says fare-svc is the only door into a settled state, and this is the one exception —
        // it is not really an exception at all. The three gateway terminals are things fare-svc
        // *observes*; cash in a driver's hand is not observable by any service, and D5' §6 draws
        // `PaymentPending --> CashOnDeliveryCollected: COD confirmed (package, P-08)` as an edge of
        // the ride machine rather than of the payment one. The driver's confirmation IS the
        // settlement, and P-14's 24-hour timer is what happens when it never comes.
        var settled = await rides.TerminateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            rideId,
            RideStates.PaymentPending,
            RideStates.CashOnDeliveryCollected,
            expectedVersion: null,
            cancellationToken);

        if (settled is null)
        {
            var current = await rides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

            await unitOfWork.RollbackAsync(cancellationToken);

            throw current is not null && !string.Equals(current.State, RideStates.PaymentPending, StringComparison.Ordinal)
                ? new MageRideException(
                    MageRideErrors.IllegalTransition,
                    $"The ride is {current.State}; cash is banked once the package has been delivered.")
                : RideProblems.Raced(current, rideId, expectedVersion: null);
        }

        // Terminal, so RideStateWriter retires every timer this service owns — the P-14 window
        // included. That is the whole mechanism: the driver's tap and the 24-hour clock race, and
        // whichever lands first leaves the other with nothing to do.
        await stateWriter.RecordAsync(
            unitOfWork,
            settled,
            RideStates.PaymentPending,
            RideTransitions.Actors.Driver,
            driverId,
            RideReasonCodes.PaymentCodCollected,
            [
                RideEvents.BuildPackage(
                    settled, RideEventTypes.CashOnDeliveryCollected,
                    Payload(settled, deliveryOtp: null), Guid.NewGuid(), now),

                // The same authorisation shape every other terminal carries (R-05), so fare-svc and
                // billing read one payload for all four. `earningPayable: true` because D5' §8.1
                // names CashOnDeliveryCollected among the three that pay.
                RideEvents.BuildSettlement(
                    settled,
                    new RideSettlementPayload(
                        PassengerId: settled.PassengerId,
                        DriverId: settled.AcceptedDriverId,
                        VehicleId: settled.AcceptedVehicleId,

                        // No `fares.ride_payments` row exists yet — fare-svc is C049/C050 — and the
                        // ride is the only aggregate that has one identifier for this settlement.
                        PaymentId: settled.Id,
                        PaymentState: RideStates.CashOnDeliveryCollected,
                        State: settled.State,
                        SettledMinor: collectedMinor,
                        Currency: settled.Currency,
                        EarningPayable: true),
                    Guid.NewGuid(),
                    now),
            ],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Driver {DriverId} banked {CollectedMinor} minor on package {RideId}; the P-14 window is closed",
            driverId, collectedMinor, rideId);

        return settled;
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Completed → PaymentPending</c> in the same transaction, exactly as
    /// <c>RideService.CompleteAsync</c> does it: the fare is owed the moment the parcel is handed
    /// over, so the ride never rests in <c>Completed</c>.
    /// </summary>
    private async Task<RideRow> HandOffToFareAsync(
        IUnitOfWork unitOfWork,
        RideRow delivered,
        string reasonCode,
        Guid? proofArtifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await stateWriter.RecordAsync(
            unitOfWork, delivered, RideStates.InProgress,
            RideTransitions.Actors.Driver, delivered.AcceptedDriverId, reasonCode, [], cancellationToken);

        var pending = await rides.AdvanceAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            delivered.Id,
            [RideStates.Completed],
            RideStates.PaymentPending,
            delivered.Version,
            requiredDriverId: null,
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.InternalError,
                "The ride moved out of Completed inside its own delivery transaction.");

        await stateWriter.RecordAsync(
            unitOfWork, pending, RideStates.Completed,
            RideTransitions.Actors.System, null, RideReasonCodes.FareHandoff,
            [
                RideEvents.Build(RideEventTypes.Completed, pending, Guid.NewGuid(), now),
                RideEvents.BuildPackage(
                    pending, RideEventTypes.PackageDelivered,
                    Payload(pending, deliveryOtp: null, proofArtifactId), Guid.NewGuid(), now),
            ],
            cancellationToken);

        return pending;
    }

    /// <summary>
    /// Charges the wrong code to the P-07 budget and says what the caller should be told.
    /// </summary>
    /// <remarks>
    /// Reached only after the matching statement declined, so either the code is wrong or the ride
    /// was never open to this attempt at all. The increment is itself a guarded <c>UPDATE</c> — it
    /// applies precisely when the digest does <em>not</em> match and the budget still has room — so
    /// the two statements can never both count one attempt, and the answer is read off which of
    /// them applied rather than off a second read of the row.
    /// </remarks>
    private async Task<MageRideException> ChargeAndDiagnoseAsync(
        IUnitOfWork unitOfWork,
        RideRow ride,
        Guid driverId,
        PackageOtpPurpose purpose,
        byte[] hash,
        IReadOnlyCollection<string> origins,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attempts = await rides.ChargePackageOtpAttemptAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            driverId,
            purpose,
            hash,
            origins,
            _options.MaxOtpAttempts,
            cancellationToken);

        if (attempts is not { } spent)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            // Nothing was charged, so the gate was not open to be guessed at. The budget is the
            // last thing checked, because a driver who is at the wrong end of the delivery should be
            // told that rather than that they are locked out.
            if (!origins.Contains(ride.State, StringComparer.Ordinal))
            {
                return new MageRideException(
                    MageRideErrors.IllegalTransition,
                    $"The ride is {ride.State}; this code is entered from {string.Join(" or ", origins)}.");
            }

            return new MageRideException(
                MageRideErrors.OtpLocked,
                $"This handoff has used all {_options.MaxOtpAttempts} of its codes. It is with support now (P-07).");
        }

        // The attempt that spent the budget is the one that raises the queue item, not the one
        // after it: the delivery is stuck the moment the last try is used, and waiting for a sixth
        // would leave a driver standing at a door with nobody notified. Raised exactly once,
        // because only one attempt can be the one that takes the count to its limit — the increment
        // above is a conditional UPDATE, so two concurrent attempts cannot both read `spent` as the
        // ceiling.
        if (spent >= _options.MaxOtpAttempts)
        {
            await outbox.WriteAsync(
                unitOfWork,
                [
                    RideEvents.BuildPackage(
                        ride,
                        RideEventTypes.PackageOtpLocked,
                        Payload(ride, deliveryOtp: null) with
                        {
                            Gate = purpose.ToString().ToLowerInvariant(),
                            Attempts = spent,
                        },
                        Guid.NewGuid(),
                        now),
                ],
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);

            logger.LogWarning(
                "Package {RideId} exhausted its {Purpose} OTP budget after {Attempts} attempts; raised to the admin " +
                "queue (P-07)",
                ride.Id, purpose, spent);
        }
        else
        {
            // The attempt is committed. Rolling it back would make the budget unenforceable: a
            // caller could guess all ten thousand codes and never spend a try.
            await unitOfWork.CommitAsync(cancellationToken);
        }

        return new MageRideException(
            MageRideErrors.InvalidOtp,
            $"That code is not this package's {purpose.ToString().ToLowerInvariant()} code. " +
            $"{_options.MaxOtpAttempts - spent} attempt(s) left.");
    }

    /// <summary>The ride, if it is a package this driver is carrying.</summary>
    private async Task<RideRow> RequirePackageAsync(
        IUnitOfWork unitOfWork, Guid rideId, Guid driverId, CancellationToken cancellationToken)
    {
        var ride = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

        if (ride is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw RideProblems.NotFound(rideId);
        }

        if (!ride.IsPackage)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.IllegalTransition,
                "This is not a package delivery. A passenger ride starts with POST /v1/rides/{rideId}/start.");
        }

        if (ride.AcceptedDriverId != driverId)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "This package was accepted by another driver.");
        }

        return ride;
    }

    private static void RequireDeliverable(RideRow? ride, Guid rideId, Guid driverId)
    {
        if (ride is null)
        {
            throw RideProblems.NotFound(rideId);
        }

        if (!ride.IsPackage)
        {
            throw new MageRideException(
                MageRideErrors.IllegalTransition, "Photo proof of delivery belongs to a package ride (P-10).");
        }

        if (ride.AcceptedDriverId != driverId)
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "This package was accepted by another driver.");
        }

        if (!string.Equals(ride.State, RideStates.InProgress, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.IllegalTransition,
                $"The ride is {ride.State}; a delivery is proved while it is {RideStates.InProgress}.");
        }
    }

    private static void RequireWellFormed(string? otp)
    {
        if (!PackageOtpCodec.IsWellFormed(otp))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["otp"] = ["otp is required and must be four digits."],
            });
        }
    }

    private static PackageEventPayload Payload(RideRow ride, string? deliveryOtp, Guid? proofArtifactId = null) =>
        new(
            PassengerId: ride.PassengerId,
            DriverId: ride.AcceptedDriverId,
            VehicleId: ride.AcceptedVehicleId,
            State: ride.State,
            PackageStatus: PackageStatuses.For(ride.State) ?? PackageStatuses.PickupPending,
            PackageSize: ride.PackageSize,
            PackageDescription: ride.PackageDescription,
            RecipientName: ride.RecipientName,
            RecipientPhone: ride.RecipientPhone,
            PaymentMethod: ride.PaymentMethod,
            DeliveryOtp: deliveryOtp,
            ProofArtifactId: proofArtifactId);
}
