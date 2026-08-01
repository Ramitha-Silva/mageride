using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Export;
using MageRide.FleetBilling.Notifications;

namespace MageRide.FleetBilling.Tests.Unit;

/// <summary>
/// The strings that cross a service boundary, asserted literally.
/// </summary>
/// <remarks>
/// A ledger key is a cross-service contract: it becomes <c>billing.journal_entries.idempotency_key</c>
/// in wallet-svc, which is UNIQUE, and that uniqueness is the only thing stopping a retried
/// settlement taking a second month's money. A well-meaning reformat of one of these would not fail
/// a build and would not fail an integration test either — it would simply start charging twice.
/// Migration 1108's header carries the same two spellings.
/// </remarks>
public sealed class LedgerKeyTests
{
    [Fact]
    public void The_invoice_settlement_key_is_the_invoice_id()
    {
        var invoiceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("fleet_invoice:11111111-2222-3333-4444-555555555555", LedgerKeys.FleetInvoice(invoiceId));
    }

    [Fact]
    public void The_topup_key_is_the_session_id()
    {
        var topupId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        Assert.Equal("fleet_topup:66666666-7777-8888-9999-aaaaaaaaaaaa", LedgerKeys.FleetTopup(topupId));
    }

    /// <summary>
    /// The journal kind migration 1108 added, and the one it did not.
    /// </summary>
    /// <remarks>
    /// <c>ck_journal_entries_kind</c> would refuse a value this class spelled differently, so the
    /// integration suite catches a typo — but it would catch it as an opaque 500 from Postgres on a
    /// settlement, which is a worse place to learn it.
    /// </remarks>
    [Fact]
    public void The_two_kinds_this_service_posts_are_the_ones_the_check_admits()
    {
        Assert.Equal("fleet_invoice", LedgerKeys.FleetInvoiceKind);
        Assert.Equal("topup", LedgerKeys.TopupKind);
    }

    /// <summary>D5' §14.4's type, which notification-svc and migration 1906 both have to agree with.</summary>
    [Fact]
    public void The_dunning_notification_type_is_the_catalogue_entry()
    {
        Assert.Equal("FLEET_INVOICE_OVERDUE", DunningNotifier.NotificationType);
    }

    /// <summary>
    /// Money reaches a template as rupees and reaches everything else as minor units.
    /// </summary>
    /// <remarks>
    /// Invariant culture in both, because a comma-decimal culture renders 300.00 as "300,00" — which
    /// in the CSV is a column break and in the push is a number an operator misreads by a factor of
    /// a hundred.
    /// </remarks>
    [Theory]
    [InlineData(0L, "0.00", "0.00")]
    [InlineData(1L, "0.01", "0.01")]
    [InlineData(30_000L, "300.00", "300.00")]
    [InlineData(150_000L, "1500.00", "1,500.00")]
    [InlineData(123_456_789L, "1234567.89", "1,234,567.89")]
    public void Minor_units_render_as_rupees(long minor, string csv, string push)
    {
        // The CSV has no thousands separator: a comma inside an unquoted field is a column break,
        // and quoting every number to keep one would make the file harder for the tools that read it.
        Assert.Equal(csv, InvoiceCsv.Rupees(minor));

        // The push does, because it lands inside a sentence a person reads.
        Assert.Equal(push, DunningNotifier.FormatRupees(minor));
    }
}
