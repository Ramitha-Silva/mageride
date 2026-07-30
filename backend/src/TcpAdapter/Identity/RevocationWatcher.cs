using System.Diagnostics;
using System.Text.Json;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Observability;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.TcpAdapter.Identity;

/// <summary>
/// What arrives on <c>prov:tracker</c> (T-12, D6' §4.2).
/// </summary>
/// <remarks>
/// The shape is provisioning-svc's <c>TrackerCredentialSignal</c>, spelled again here because this
/// project holds no reference to that one — the same situation as position-processor-svc's copy of
/// dispatch-svc's <c>AVAILABLE</c>. The member names are what matter: the publisher serialises with the
/// kernel's camelCase options, so a rename on either side turns every field null and the socket simply
/// never closes. The test suite asserts a signal serialised by the real record deserialises into this.
/// </remarks>
/// <param name="Type"><c>tracker.bound</c> | <c>tracker.revoked</c> — the same names the outbox uses.</param>
/// <param name="Imei">The device. What this service matches an open socket on.</param>
/// <param name="VehicleId">The vehicle it was bound to.</param>
/// <param name="Serials">Credential serials the message invalidates.</param>
/// <param name="Reason">Why, for the log line.</param>
/// <param name="At">When provisioning-svc committed it.</param>
public sealed record TrackerCredentialSignal(
    string? Type,
    string? Imei,
    Guid VehicleId,
    IReadOnlyList<string>? Serials,
    string? Reason,
    DateTimeOffset At);

/// <summary>
/// Force-closes a device's socket within a second of its credential being released (T-12).
/// </summary>
/// <remarks>
/// <para>
/// ADD §7.7.3: "On revocation event, the adapter receives a pub/sub message and force-closes any
/// matching socket within 1 s." That budget is why this is a subscription and not a poll — the durable
/// twin is the <c>tracker.revoked</c> row on <c>provisioning.events</c>, and a consumer group's lag
/// alone would not meet it.
/// </para>
/// <para>
/// <b>Three events close a socket and two do not.</b> <c>tracker.revoked</c> is the obvious one, and
/// provisioning-svc publishes it for an unbind, an admin decommission and a quarantine alike.
/// <c>tracker.bound</c> also closes: an IMEI being bound while a socket for it is open means the device
/// has been moved to another vehicle, and that socket is publishing under the old one. What must
/// <b>not</b> close is a rotation — "rotation is not revocation, and conflating them bricks devices"
/// (C030): the replacement credential is minted fourteen days early and the outgoing one stays valid,
/// precisely so a tracker parked out of coverage can come back and collect it.
/// </para>
/// <para>
/// <b>Redis pub/sub is fire-and-forget, and that is accounted for.</b> A pod that was restarting when
/// the message went out never sees it; what catches that device is the five-minute re-validation on its
/// socket (ADD §7.7.3) and the deletion of <c>imei:{imei}</c>, which makes its next connect go to
/// <c>validate</c> and be refused. The fast path is an optimisation on a slow path that is already
/// correct.
/// </para>
/// </remarks>
public sealed class RevocationWatcher(
    IConnectionMultiplexer redis,
    SessionRegistry registry,
    VehicleProfileCache profiles,
    IOptions<AdapterOptions> options,
    ILogger<RevocationWatcher> logger) : BackgroundService
{
    /// <summary>Event names that release a credential. Spelled here — see <see cref="TrackerCredentialSignal"/>.</summary>
    public const string TrackerRevoked = "tracker.revoked";

    /// <summary>A (re)bind. Closes an open socket because the vehicle behind the IMEI may have changed.</summary>
    public const string TrackerBound = "tracker.bound";

    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var channel = RedisChannel.Literal(RedisKeys.TrackerCredentialChannel);

        try
        {
            var subscriber = redis.GetSubscriber();

            await subscriber.SubscribeAsync(channel, (_, value) => Handle(value));

            logger.LogInformation(
                "Watching {Channel} for credential releases; the T-12 budget is {Budget}",
                RedisKeys.TrackerCredentialChannel, _options.RevocationCloseBudget);

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
        catch (RedisException exception)
        {
            // Loud: the fast half of T-12 is now off and nothing says so from the outside. What is left
            // is the five-minute re-validation and the cache's own TTL.
            logger.LogError(
                exception,
                "Could not subscribe to {Channel}. Revocation now takes up to {Interval} to reach an open " +
                "socket instead of the {Budget} ADD §7.7.3 allows.",
                RedisKeys.TrackerCredentialChannel, _options.RevalidateInterval, _options.RevocationCloseBudget);
        }
        finally
        {
            try
            {
                await redis.GetSubscriber().UnsubscribeAsync(channel);
            }
            catch (RedisException)
            {
                // Shutting down against a broker that is already gone.
            }
        }
    }

    /// <summary>Parses one message and acts on it. Internal so a test can drive it without Redis.</summary>
    internal void Handle(RedisValue value)
    {
        TrackerCredentialSignal? signal;

        try
        {
            signal = JsonSerializer.Deserialize<TrackerCredentialSignal>(value.ToString(), MageRideJson.Options);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "A message on {Channel} could not be read", RedisKeys.TrackerCredentialChannel);
            return;
        }

        if (signal?.Imei is null or { Length: 0 })
        {
            return;
        }

        if (signal.Type is not (TrackerRevoked or TrackerBound))
        {
            // A rotation or a source switch. Deliberately not a close — see the class remarks.
            return;
        }

        // The vehicle's cached mode may be about to change hands with the binding.
        if (signal.VehicleId != Guid.Empty)
        {
            profiles.Forget(signal.VehicleId);
        }

        var forgotten = registry.ForgetDatagram(signal.Imei);
        var session = registry.ForImei(signal.Imei);

        if (session is null)
        {
            if (forgotten)
            {
                logger.LogInformation(
                    "Dropped the cached UDP authorisation for IMEI {Imei} on {Type}", signal.Imei, signal.Type);
            }

            return;
        }

        // Fire and forget on purpose: the callback runs on StackExchange.Redis's subscription pump and
        // awaiting a socket teardown on it would stall every other message on the channel. The budget
        // is enforced inside CloseAsync.
        _ = CloseAsync(session, signal);
    }

    private async Task CloseAsync(ITrackerSession session, TrackerCredentialSignal signal)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await session.CloseAsync($"{signal.Type} ({signal.Reason ?? "no reason given"})");

            var elapsed = Stopwatch.GetElapsedTime(started);

            AdapterDiagnostics.RevocationClosures.Add(
                1, AdapterDiagnostics.Tag("type", signal.Type ?? "unknown"));

            AdapterDiagnostics.RevocationLatencyMs.Record(elapsed.TotalMilliseconds);

            if (elapsed > _options.RevocationCloseBudget)
            {
                logger.LogWarning(
                    "Closing IMEI {Imei}'s socket on {Type} took {Elapsed} — over the {Budget} T-12 allows",
                    signal.Imei, signal.Type, elapsed, _options.RevocationCloseBudget);
            }
            else
            {
                logger.LogInformation(
                    "Closed IMEI {Imei}'s socket on {Type} in {Elapsed}", signal.Imei, signal.Type, elapsed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not close IMEI {Imei}'s socket on {Type}", signal.Imei, signal.Type);
        }
    }
}
