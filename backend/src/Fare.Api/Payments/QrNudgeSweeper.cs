using MageRide.Fare.Configuration;
using MageRide.Fare.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Payments;

/// <summary>
/// AL-47 / US-26.1's "+5 min" — the driver who has not answered a passenger's driver-QR claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sweep, not a timer row.</b> <c>ix_ridepay_qr_unconfirmed</c> (migration 1002) is a partial
/// index over <c>state = 'QrClaimedByPassenger'</c> ordered by <c>qr_claimed_at</c>, and its own
/// comment says why: "D5 escalates these on a timer, so the scan is by age". The queue is the index;
/// a durable per-claim row would be a second representation of the same fact.
/// </para>
/// <para>
/// <b>What it cannot do yet is push.</b> notification-svc (C051/C052) owns FCM and APNs and does not
/// exist; there is no <c>comms</c> outbound queue table either, only registration tokens. So this
/// sweep identifies who should be nudged and says so at warning level, and the push itself is named
/// in the C050 handoff rather than faked. Everything the notification needs — the driver, the ride,
/// the amount and the claim instant — is on the row it finds.
/// </para>
/// <para>
/// <b>One nudge per claim, without a column to remember it.</b> The window is claims whose age
/// crossed the threshold during <em>this</em> pass — between <c>threshold + interval</c> and
/// <c>threshold</c> old — so a claim is picked up once rather than on every pass for as long as it
/// stays unanswered. A restart that straddles a window can miss one; the alternative is a
/// <c>nudged_at</c> column no spec asks for, and the failure of missing a reminder is smaller than
/// the failure of sending one every minute for an hour.
/// </para>
/// </remarks>
internal sealed class QrNudgeSweeper(
    IServiceProvider services,
    IOptions<FareOptions> options,
    TimeProvider clock,
    ILogger<QrNudgeSweeper> logger) : BackgroundService
{
    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.QrNudgeEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.QrNudgeInterval, clock);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Swallowed on purpose: an unhandled exception here takes the BackgroundService down
                // for the process's lifetime, so one bad database moment would silence every later
                // nudge until somebody restarted the pod.
                logger.LogError(exception, "The AL-47 driver-QR nudge sweep failed. Retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<IRidePaymentRepository>();

        var now = clock.GetUtcNow();
        var due = now - _options.QrNudgeAfter;

        var claims = await payments.ListUnconfirmedQrClaimsAsync(
            due, _options.QrNudgeBatchSize, cancellationToken);

        // Only the ones that crossed the threshold since the last pass — see the class remarks.
        var window = due - _options.QrNudgeInterval;
        var fresh = claims.Where(c => c.QrClaimedAt is { } at && at > window).ToArray();

        foreach (var claim in fresh)
        {
            logger.LogWarning(
                "Driver-QR claim on ride {RideId} (payment {PaymentId}, {AmountMinor} {Currency}) has been "
                + "unconfirmed since {ClaimedAt:O}. The driver needs the US-26.1 re-push; notification-svc "
                + "(C051/C052) is not built, so this is the record of it.",
                claim.RideId,
                claim.Id,
                claim.AmountMinor,
                claim.Currency,
                claim.QrClaimedAt);
        }

        return fresh.Length;
    }
}
