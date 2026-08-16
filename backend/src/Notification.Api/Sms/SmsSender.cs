using MageRide.Notification.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sms;

/// <summary>Sends one SMS, choosing between the two D6' §7.3 dispatch shapes.</summary>
public interface ISmsSender
{
    /// <summary>
    /// Primary, then secondary if the primary refused (D6' §7.3). Everything but an SOS.
    /// </summary>
    Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken);

    /// <summary>
    /// D-33: both gateways at once, resolving on whichever lands first.
    /// </summary>
    Task<SmsResult> SendUrgentAsync(string phone, string message, CancellationToken cancellationToken);

    /// <summary>True when a second gateway exists to send D-33's parallel copy through.</summary>
    bool HasSecondGateway { get; }
}

/// <summary>
/// The two shapes, and the reason there are two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequential for everything, parallel for an SOS, and the difference is the SLO.</b> D6' §7.3
/// puts the retry on the primary and the secondary behind it — the right trade when an extra
/// message costs money and a second of latency costs nothing. D-33 inverts both: "primary +
/// secondary gateway IN PARALLEL; p99 ≤ 5 s; whichever delivers first". An emergency alert that
/// waits out the primary's two attempts and its timeout has already spent more than the whole
/// budget, so the SOS path pays for two messages and takes the first answer.
/// </para>
/// <para>
/// <b>The parallel send does not wait for the loser.</b> `Task.WhenAny` over the two, and the
/// straggler is left running with its result observed on a continuation so a faulted task cannot
/// surface later as an unobserved exception. The caller gets the winner; the loser's message is
/// still delivered, which is the accepted cost of the design and not a bug.
/// </para>
/// <para>
/// <b>Both gateways failing is a failure, not a silent success.</b> The delivery worker records it
/// and retries within the D-27 schedule; nothing here pretends an emergency message went out.
/// </para>
/// </remarks>
public sealed class SmsSender(
    IEnumerable<ISmsGateway> gateways,
    IOptions<SmsOptions> options,
    ILogger<SmsSender> logger) : ISmsSender
{
    private readonly SmsOptions _sms = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IReadOnlyList<ISmsGateway> _gateways = [.. gateways];

    /// <summary>
    /// The primary — whichever gateway <c>Sms:Provider</c> names.
    /// </summary>
    /// <remarks>
    /// Resolved by NAME from the provider rather than by "the one that is not the secondary": the
    /// composition root registers exactly one primary, so a lookup that took whatever it found
    /// would keep working after a misconfiguration instead of reporting one. An unknown provider
    /// therefore yields no primary and every send fails loudly, which is the correct reading of a
    /// deployment that named a gateway nobody implements.
    /// </remarks>
    private ISmsGateway? Primary => Find(PrimaryNameFor(_sms.Provider));

    /// <summary>The gateway name a <c>Sms:Provider</c> value selects.</summary>
    internal static string PrimaryNameFor(string? provider) =>
        provider switch
        {
            not null when provider.Equals(SmsOptions.DevProvider, StringComparison.OrdinalIgnoreCase)
                => SmsGatewayNames.Dev,
            not null when provider.Equals(SmsOptions.FitSmsProvider, StringComparison.OrdinalIgnoreCase)
                => SmsGatewayNames.FitSms,
            not null when provider.Equals(SmsOptions.NotifyLkProvider, StringComparison.OrdinalIgnoreCase)
                => SmsGatewayNames.NotifyLk,
            _ => string.Empty,
        };

    private ISmsGateway? Secondary
    {
        get
        {
            var secondary = Find(SmsGatewayNames.Secondary);
            return secondary?.IsConfigured == true ? secondary : null;
        }
    }

    public bool HasSecondGateway => Secondary is not null;

    public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var primary = Primary;

        if (primary is null)
        {
            return SmsResult.Failed("none", "No SMS gateway is registered.");
        }

        var result = await primary.SendAsync(phone, message, cancellationToken);

        if (result.Delivered)
        {
            return result;
        }

        var secondary = Secondary;

        if (secondary is null)
        {
            return result;
        }

        logger.LogWarning(
            "The primary SMS gateway ({Primary}) refused a message to {Phone} ({Error}); falling back to {Secondary} (D6' §7.3).",
            primary.Name,
            NotifyLkSmsGateway.Redact(phone),
            result.Error,
            secondary.Name);

        var fallback = await secondary.SendAsync(phone, message, cancellationToken);

        return fallback.Delivered
            ? fallback
            : SmsResult.Failed(
                $"{primary.Name}+{secondary.Name}",
                $"Both gateways refused: {result.Error} / {fallback.Error}");
    }

    public async Task<SmsResult> SendUrgentAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var primary = Primary;
        var secondary = Secondary;

        if (primary is null)
        {
            return SmsResult.Failed("none", "No SMS gateway is registered.");
        }

        if (secondary is null)
        {
            // D-33 asks for two and there is one. The message still goes, and the log says the SLO
            // now rests on a single gateway — which is the fact somebody investigating a slow SOS
            // needs, and which start-up has already announced once.
            logger.LogWarning(
                "An urgent (D-33) message is going through {Primary} alone: no secondary gateway is configured, "
                + "so the p99 ≤ 5 s promise has no second chance behind it.",
                primary.Name);

            return await primary.SendAsync(phone, message, cancellationToken);
        }

        // Both names are recorded before either answers, because that is the fact D-33 is about:
        // the message was handed to two gateways at the same time. Which one came back first is a
        // separate fact and is `SmsResult.Gateway`.
        string[] attempted = [primary.Name, secondary.Name];

        var first = primary.SendAsync(phone, message, cancellationToken);
        var second = secondary.SendAsync(phone, message, cancellationToken);

        var winner = await Task.WhenAny(first, second);
        var result = await winner;

        if (result.Delivered)
        {
            Observe(winner == first ? second : first);
            return result with { Attempted = attempted };
        }

        // The first answer was a refusal, so wait for the other one — the point of sending twice is
        // that either may land.
        var other = await (winner == first ? second : first);

        if (other.Delivered)
        {
            return other with { Attempted = attempted };
        }

        return SmsResult.Failed(
            $"{primary.Name}+{secondary.Name}",
            $"Both gateways refused in parallel: {result.Error} / {other.Error}") with { Attempted = attempted };
    }

    private ISmsGateway? Find(string name) =>
        _gateways.FirstOrDefault(gateway => string.Equals(gateway.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Keeps the loser's task from becoming an unobserved exception. It is deliberately not awaited:
    /// D-33 resolves on the first delivery, and blocking on the slower gateway would give away the
    /// latency the parallel send was bought with.
    /// </summary>
    private void Observe(Task<SmsResult> straggler) =>
        _ = straggler.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    logger.LogWarning(task.Exception, "The second SMS gateway faulted after the first had delivered.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
