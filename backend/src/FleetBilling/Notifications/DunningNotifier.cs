using System.Globalization;
using System.Net.Http.Json;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Notifications;

/// <summary>Tells the organisation's Owners that a bill is late.</summary>
internal interface IDunningNotifier
{
    /// <summary><see langword="true"/> when notification-svc accepted the notice.</summary>
    Task<bool> NotifyAsync(OverdueInvoice invoice, int daysOverdue, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDunningNotifier"/>
/// <remarks>
/// <para>
/// <b>No user-facing string is composed here (D-26).</b> The call carries a <c>notificationType</c>
/// and a bag of values; notification-svc resolves the template (migration 1906 seeds
/// <c>fleet_invoice_overdue</c> in Sinhala, Tamil and English) and each recipient's own language.
/// A sentence built in this service would be an English sentence in every operator's push.
/// </para>
/// <para>
/// <b>The amount is formatted once, here, as rupees.</b> Everything on this platform moves money as
/// integer minor units, and <c>{{amount}}</c> lands inside a sentence — so the one place the
/// conversion can happen is where the template's values are assembled. Invariant culture, because
/// the string is a number in a message and not a locale-formatted currency: a comma-decimal culture
/// would render "Rs 3.000,00" into a Sinhala sentence.
/// </para>
/// <para>
/// <b>Best effort, and not re-queued.</b> The invoice is already OVERDUE and the Fleet Portal can
/// draw it; a retry loop here would push an operator repeatedly about a bill whose state has not
/// changed, and <c>FleetBilling:DunningInterval</c> already decides when to say it again.
/// </para>
/// </remarks>
internal sealed class DunningNotifier(
    IHttpClientFactory clients,
    IOptions<FleetBillingOptions> options,
    ILogger<DunningNotifier> logger) : IDunningNotifier
{
    /// <summary>
    /// D5' §14.4's type. <b>Δ C060</b> — added to <c>NotificationCatalogue</c> in the same change,
    /// with a trilingual template in migration 1906.
    /// </summary>
    /// <remarks>
    /// Not <c>LOW_BALANCE</c> and not <c>TOP_UP_REQUIRED</c>: both are US-9.9 / D5' §9.4's driver
    /// warnings, wallet-svc emits them edge-triggered on a driver account, and their bodies talk
    /// about the next trip. An organisation takes no trips and may have a healthy balance and simply
    /// not have paid.
    /// </remarks>
    public const string NotificationType = "FLEET_INVOICE_OVERDUE";

    /// <summary>The named client for notification-svc's internal plane.</summary>
    public const string HttpClientName = "notification-svc";

    /// <summary>The guard header every internal plane on the platform carries (C008).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<bool> NotifyAsync(
        OverdueInvoice invoice, int daysOverdue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (string.IsNullOrWhiteSpace(_options.NotificationBaseUrl))
        {
            logger.LogWarning(
                "FleetBilling:NotificationBaseUrl is not configured, so nobody was told that invoice "
                + "{InvoiceId} is {DaysOverdue} day(s) overdue. The invoice is OVERDUE and the Fleet Portal "
                + "can still show it.",
                invoice.InvoiceId,
                daysOverdue);

            return false;
        }

        if (invoice.OwnerIds.Count == 0)
        {
            logger.LogWarning(
                "Invoice {InvoiceId} is overdue and the organisation has no Owner to tell.", invoice.InvoiceId);

            return false;
        }

        try
        {
            var client = clients.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/notify/send")
            {
                Content = JsonContent.Create(
                    new
                    {
                        notificationType = NotificationType,
                        recipients = invoice.OwnerIds.Select(id => id.ToString()).ToArray(),
                        data = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["invoiceId"] = invoice.InvoiceId.ToString(),
                            ["fleetId"] = invoice.FleetId.ToString(),
                            ["fleetName"] = invoice.FleetName,
                            ["periodMonth"] = invoice.PeriodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                            ["amount"] = FormatRupees(invoice.TotalMinor),
                            ["amountMinor"] = invoice.TotalMinor.ToString(CultureInfo.InvariantCulture),
                            ["daysOverdue"] = daysOverdue.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    options: MageRideJson.Options),
            };

            if (!string.IsNullOrWhiteSpace(_options.NotificationInternalApiKey))
            {
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.NotificationInternalApiKey);
            }

            // The key is the invoice and the reminder round, not a random value: a sweep that ran
            // twice for one round must not put two identical notices on an operator's phone, and a
            // genuinely later reminder is a different round and is meant to arrive.
            request.Headers.TryAddWithoutValidation(
                MageRideHeaders.IdempotencyKey, $"fleet-invoice-overdue:{invoice.InvoiceId}:{daysOverdue}");

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            logger.LogWarning(
                "notification-svc answered {Status} to the dunning notice for invoice {InvoiceId}; the invoice "
                + "stays OVERDUE and nobody was pushed.",
                (int)response.StatusCode,
                invoice.InvoiceId);

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "notification-svc could not be reached for the dunning notice on invoice {InvoiceId}.",
                invoice.InvoiceId);

            return false;
        }
    }

    /// <summary>Minor units as rupees, with two decimals and thousands separators.</summary>
    internal static string FormatRupees(long minor) =>
        (minor / 100m).ToString("N2", CultureInfo.InvariantCulture);
}
