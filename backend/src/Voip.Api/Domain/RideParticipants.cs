namespace MageRide.Voip.Domain;

/// <summary>
/// The parties to one ride, as far as calling is concerned (<c>rides.rides</c>, D4' §5).
/// </summary>
/// <param name="PassengerId">The booking account, on all three ride kinds (C037's rule).</param>
/// <param name="BookerId">
/// Equal to <see cref="PassengerId"/> unless the ride is a proxy booking (P-01). <b>Never a call
/// participant in its own right</b> — see <see cref="RiderId"/>.
/// </param>
/// <param name="RiderId">
/// The person actually in the vehicle, when they have an account. <see langword="null"/> on a proxy
/// booking for an unregistered rider (P-03), whose number is stored only as a digest.
/// </param>
public sealed record RideParticipants(
    Guid RideId,
    Guid PassengerId,
    Guid BookerId,
    Guid? RiderId,
    bool IsProxy,
    Guid? AcceptedDriverId,
    string State)
{
    /// <summary>Whether the ride has reached a state it never leaves.</summary>
    public bool IsTerminal => RideStates.IsTerminal(State);

    /// <summary>
    /// The account on the passenger side of the call — <b>the rider, never the booker</b> (P-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On an ordinary booking the booker, the passenger and the rider are one account and this is
    /// simply that account. On a proxy booking (P-01) they are not: somebody booked a ride for
    /// somebody else, and D6' §6 and P-05 both say the driver is bound to the person in the
    /// vehicle. <c>rider_id</c> is therefore the only source for it, and there is deliberately no
    /// fallback to <c>booker_id</c> — a fallback is exactly how the booker ends up on the call.
    /// </para>
    /// <para>
    /// <see langword="null"/> means the rider has no account (P-03), so there is nobody to admit to
    /// a room. That ride has no in-app call at all: the fallback is the direct dial, and even that
    /// is unavailable here because P-03 keeps only a digest of their number — a conflict ride-svc
    /// records from its own side and this service can only refuse.
    /// </para>
    /// </remarks>
    public Guid? RiderIdentity => IsProxy ? RiderId : RiderId ?? PassengerId;

    /// <summary>Which side of the call <paramref name="userId"/> is on, or null if neither.</summary>
    /// <remarks>
    /// <b>Two identities, not three.</b> A proxy booker is not a participant: P-05 binds the driver
    /// to the rider, and admitting the booker to the same room is the one thing that fence forbids.
    /// They keep every other channel — the ride detail, the tracking link, support — and they do not
    /// get a voice path to the driver.
    /// </remarks>
    public CallParty? PartyFor(Guid userId)
    {
        if (AcceptedDriverId == userId)
        {
            return CallParty.Driver;
        }

        return RiderIdentity == userId ? CallParty.Rider : null;
    }
}

/// <summary>Which end of the call a caller is on.</summary>
public enum CallParty
{
    /// <summary>The accepted driver. Their counterparty is the rider.</summary>
    Driver,

    /// <summary>The person in the vehicle. Their counterparty is the driver.</summary>
    Rider,
}
