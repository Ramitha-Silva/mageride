using MageRide.Shared.Errors;
using MageRide.Voip.Configuration;
using MageRide.Voip.Domain;
using MageRide.Voip.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Voip.Signalling;

/// <summary>A minted token plus who the caller will be talking to.</summary>
public sealed record IssuedToken(SignallingToken Token, CallParty Callee);

/// <summary>A logged call, with a session when one was started.</summary>
public sealed record StartedCall(Guid CallId, string CallType, SignallingToken? Session);

/// <summary>The two decisions this service makes: who may call, and what to hand them.</summary>
public interface ICallService
{
    Task<IssuedToken> IssueTokenAsync(Guid rideId, Guid callerId, CancellationToken cancellationToken);

    Task<StartedCall> StartCallAsync(
        Guid rideId, Guid callerId, string calleeRole, string callType, CancellationToken cancellationToken);

    Task RecordOutcomeAsync(Guid callId, Guid callerId, string outcome, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Every refusal is decided here, once.</b> Both routes ask the same three questions in the same
/// order — does the ride exist, is the caller one of its two call parties (P-05), has the ride
/// ended — so `/v1/voip/token` and `/v1/calls/start` cannot come to different conclusions about
/// the same ride.
/// </para>
/// <para>
/// <b>`direct_dial` starts nothing.</b> It is a row and a return; there is no PSTN leg, no bridge
/// and no number in this process (AL-48). The client dialled the number ride-svc gave it and is
/// telling us afterwards, which is the most this platform can ever know about that call.
/// </para>
/// </remarks>
internal sealed class CallService : ICallService
{
    private readonly IVoipRepository _repository;
    private readonly ILiveKitTokenMinter _tokens;
    private readonly VoipOptions _options;
    private readonly ILogger<CallService> _logger;

    public CallService(
        IVoipRepository repository,
        ILiveKitTokenMinter tokens,
        IOptions<VoipOptions> options,
        ILogger<CallService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One room per ride (D3' voip-svc: <c>ride_{id}</c>).</summary>
    public static string RoomFor(Guid rideId) => $"ride_{rideId:D}";

    public async Task<IssuedToken> IssueTokenAsync(
        Guid rideId, Guid callerId, CancellationToken cancellationToken)
    {
        var (ride, party) = await ResolveAsync(rideId, callerId, cancellationToken);

        return new IssuedToken(Mint(ride, party), Counterparty(party));
    }

    public async Task<StartedCall> StartCallAsync(
        Guid rideId, Guid callerId, string calleeRole, string callType, CancellationToken cancellationToken)
    {
        if (!CallTypes.IsKnown(callType))
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                $"callType must be one of {string.Join(", ", CallTypes.All)}. Number masking was withdrawn by "
                + "AL-48: there is no masked PSTN bridge to ask for.");
        }

        if (!CalleeRoles.IsKnown(calleeRole))
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                $"calleeRole must be one of {string.Join(", ", CalleeRoles.All)}.");
        }

        var (ride, party) = await ResolveAsync(rideId, callerId, cancellationToken);

        if (callType == CallTypes.DirectDial)
        {
            // A tap on a `tel:` link, reported after the fact. Nothing is started and nothing can
            // be: the number came from ride-svc's ride detail and the PSTN leg is the carrier's.
            var tapId = await _repository.LogCallAsync(
                rideId, callerId, calleeRole, callType, cancellationToken);

            return new StartedCall(tapId, callType, Session: null);
        }

        if (!CalleeRoles.CanBeVoip(calleeRole))
        {
            // A parcel's sender or recipient may have no account at all (P-09), so there is nobody
            // to admit to a room. Their Call button is a `tel:` link and always was.
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                $"'{calleeRole}' cannot be reached in-app; that call is a direct dial (AL-48).");
        }

        var token = Mint(ride, party);
        var session = await _repository.OpenSessionAsync(rideId, token.RoomName, cancellationToken);

        var callId = await _repository.LogCallAsync(rideId, callerId, calleeRole, callType, cancellationToken);

        _logger.LogInformation(
            "Call {CallId} started on ride {RideId} in room {Room} (session {SessionId}).",
            callId, rideId, token.RoomName, session.Id);

        return new StartedCall(callId, callType, token);
    }

    public async Task RecordOutcomeAsync(
        Guid callId, Guid callerId, string outcome, CancellationToken cancellationToken)
    {
        if (!CallOutcomes.IsKnown(outcome))
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                $"outcome must be one of {string.Join(", ", CallOutcomes.All)}.");
        }

        if (!await _repository.CloseCallAsync(callId, callerId, outcome, cancellationToken))
        {
            // Somebody else's call and an already-closed one answer identically: a call id is a
            // guessable identifier and "that id exists" is itself something a stranger should not
            // be able to learn about two other people's conversation.
            throw new MageRideException(MageRideErrors.NotFound, "No such call.");
        }

        if (outcome == CallOutcomes.VoipFailed)
        {
            // The signal ADD §14 hangs the direct-dial prompt on. Logged at warning because a rate
            // of these is the operational fact — ADD §16 has a p95 call-setup SLO and nothing else
            // on the platform can see a call that never connected.
            _logger.LogWarning(
                "In-app call {CallId} failed to connect; the client will offer the direct-dial fallback (AL-48).",
                callId);
        }
    }

    /// <summary>The three questions, asked once.</summary>
    private async Task<(RideParticipants Ride, CallParty Party)> ResolveAsync(
        Guid rideId, Guid callerId, CancellationToken cancellationToken)
    {
        var ride = await _repository.FindRideAsync(rideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, "No such ride.");

        if (ride.PartyFor(callerId) is not { } party)
        {
            // Deliberately the same answer for a stranger and for a proxy booker: P-05 makes the
            // booker a non-participant *of the call*, and spelling that out would tell a caller
            // which ride they are adjacent to.
            throw new MageRideException(
                MageRideErrors.NotRideParticipant,
                "The in-app call connects the driver and the rider (P-05).");
        }

        // Checked after participation, so a stranger cannot learn a ride's state by watching which
        // refusal they get.
        if (ride.IsTerminal)
        {
            throw new MageRideException(MageRideErrors.RideTerminal, "This ride has ended.");
        }

        // Before a driver accepts there is no counterparty — the room would have one person in it,
        // and AL-48 withholds the counterparty's number until Accepted for the same reason. The
        // apps only show the Call action post-accept, so this is a backstop rather than a path.
        if (ride.AcceptedDriverId is null)
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed, "No driver has accepted this ride yet.");
        }

        // A proxy ride whose rider never registered (P-03) has no second identity to admit. The
        // driver is a participant and is still refused, because a token into an empty room is a
        // call that rings for ever — and there is no fallback either: P-03 keeps only a digest of
        // that rider's number, so ride-svc withholds it too. AL-48 and P-03 conflict in exactly
        // this cell and P-03 wins, which is the same conclusion ride-svc reached from its side.
        if (ride.RiderIdentity is null)
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                "This ride's rider has no MageRide account, so there is no in-app call (P-03).");
        }

        return (ride, party);
    }

    private SignallingToken Mint(RideParticipants ride, CallParty party)
    {
        if (!_tokens.IsConfigured)
        {
            // The VoIP-failure signal, at its earliest and clearest point: the client shows
            // "Call normally instead?" and dials the number ride-svc gave it (ADD §14, AL-48).
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "In-app calling is unavailable. Call the number on the ride instead.");
        }

        // The identity is the whole of P-05: on the passenger side it is the RIDER's account, so a
        // proxy booker has no identity to present even if they somehow obtained a token.
        var identity = party switch
        {
            CallParty.Driver => ride.AcceptedDriverId!.Value,
            _ => ride.RiderIdentity!.Value,
        };

        return _tokens.Mint(
            RoomFor(ride.RideId),
            identity.ToString("D"),
            party == CallParty.Driver ? "driver" : "rider",
            _options.TokenTtl);
    }

    private static CallParty Counterparty(CallParty party) =>
        party == CallParty.Driver ? CallParty.Rider : CallParty.Driver;
}
