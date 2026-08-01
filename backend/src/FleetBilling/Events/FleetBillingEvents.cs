using System.Text.Json;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.FleetBilling.Events;

/// <summary>
/// The three event types this service produces onto <c>fleet.events</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by fleetId, on C044's topic.</b> <c>fleet.events</c> was opened by fleet-health-svc for
/// <c>fleet.health_alert</c> and is described in <see cref="EventTopics.FleetEvents"/> as
/// organisation-scoped and ordered per organisation, which is exactly what these need: an invoice
/// paid and the invoice issued before it have to arrive in that order or the Fleet Portal's billing
/// badge flickers backwards. A topic of this service's own would be a second partition space for
/// facts about the same aggregate, which cannot be ordered against C044's.
/// </para>
/// <para>
/// <b>Nobody consumes these yet, and that is stated rather than hidden.</b> The Fleet Portal
/// (C115) reads the invoice list over HTTP; these events exist because C060's deliverable is
/// "dunning / overdue signalling to the Fleet Portal <em>and</em> notification-svc" and because a
/// state change nobody can subscribe to is a state change every consumer has to poll for. The
/// notification half does <b>not</b> go through here — it is a direct call to notification-svc's
/// internal plane, for C059's reason: notification-svc consumes no <c>fleet.events</c> and adding a
/// consumer there for one type would be a second delivery path for a message that already has one.
/// </para>
/// </remarks>
internal static class FleetBillingEventTypes
{
    /// <summary>An invoice has been raised for a month. Carries the total and the line count.</summary>
    public const string InvoiceIssued = "fleet.invoice_issued";

    /// <summary>An invoice has been settled against the fleet wallet. Carries the journal entry.</summary>
    public const string InvoicePaid = "fleet.invoice_paid";

    /// <summary>An invoice has passed its payment term. The Fleet Portal's dunning banner.</summary>
    public const string InvoiceOverdue = "fleet.invoice_overdue";
}

/// <summary>Builds the outbox rows. One shape per type, serialised once.</summary>
internal static class FleetBillingEvents
{
    public static OutboxRecord Issued(FleetInvoice invoice, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return OutboxRecord.Create(
            invoice.FleetId,
            FleetBillingEventTypes.InvoiceIssued,
            Serialise(new
            {
                eventType = FleetBillingEventTypes.InvoiceIssued,
                invoiceId = invoice.Id,
                fleetId = invoice.FleetId,
                periodMonth = invoice.PeriodMonth,
                amountMinor = invoice.TotalMinor,
                currency = invoice.Currency,
                status = invoice.Status,
                vehicleCount = invoice.VehicleCount,
                dueAt = invoice.DueAt,
                at,
            }));
    }

    public static OutboxRecord Paid(
        FleetInvoice invoice, Guid journalEntryId, long balanceAfterMinor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return OutboxRecord.Create(
            invoice.FleetId,
            FleetBillingEventTypes.InvoicePaid,
            Serialise(new
            {
                eventType = FleetBillingEventTypes.InvoicePaid,
                invoiceId = invoice.Id,
                fleetId = invoice.FleetId,
                periodMonth = invoice.PeriodMonth,
                amountMinor = invoice.TotalMinor,
                currency = invoice.Currency,
                journalEntryId,
                balanceAfterMinor,
                at,
            }));
    }

    /// <param name="notificationType">
    /// D5' §14.4's type, carried so a consumer never has to compose a user-facing string from an
    /// event (D-26). notification-svc resolves the wording and the recipient's language.
    /// </param>
    public static OutboxRecord Overdue(OverdueInvoice invoice, int daysOverdue, string notificationType, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return OutboxRecord.Create(
            invoice.FleetId,
            FleetBillingEventTypes.InvoiceOverdue,
            Serialise(new
            {
                eventType = FleetBillingEventTypes.InvoiceOverdue,
                invoiceId = invoice.InvoiceId,
                fleetId = invoice.FleetId,
                periodMonth = invoice.PeriodMonth,
                amountMinor = invoice.TotalMinor,
                dueAt = invoice.DueAt,
                daysOverdue,
                notificationType,
                at,
            }));
    }

    private static string Serialise(object payload) => JsonSerializer.Serialize(payload, MageRideJson.Options);
}
