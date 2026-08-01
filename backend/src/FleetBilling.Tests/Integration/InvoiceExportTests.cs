using System.Globalization;
using System.Net;
using System.Text;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// SCR-FP-010's Download: "CSV/PDF export for the Fleet Portal billing screen".
/// </summary>
/// <remarks>
/// Driven through the running service rather than against the renderers directly, because what the
/// deliverable promises is a downloadable file — the content type, the filename and the bytes an
/// operator's browser receives are as much a part of it as the table inside.
/// </remarks>
[Collection<FleetBillingCollection>]
public sealed class InvoiceExportTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// The DoD's first clause, rendered as a file: an operator who sums the amount column gets the
    /// TOTAL row, and the TOTAL row is what the wallet was debited.
    /// </summary>
    [Fact]
    public async Task The_csv_rows_sum_to_its_total_row()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync(name: "C060 Galle Transit");
        var vanA = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var vanB = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var bus = await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var invoiceId = await FirstInvoiceAsync(harness, fleet);

        var (bytes, contentType) = await harness.DownloadAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=csv", fleet.Bearer);

        Assert.Equal("text/csv", contentType);

        // UTF-8 BOM, so a spreadsheet opens a Sinhala or Tamil organisation name correctly. Trimmed
        // before parsing, because a BOM in front of the first `#` makes it not a comment line.
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);

        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        Assert.Contains("# fleet,C060 Galle Transit", text, StringComparison.Ordinal);
        Assert.Contains("# periodMonth,2026-07", text, StringComparison.Ordinal);
        Assert.Contains("registrationNumber,vehicleType,status,amount,amountMinor,currency", text, StringComparison.Ordinal);

        var rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Skip(1)
            .ToArray();

        var vehicleRows = rows.Where(row => !row.StartsWith("TOTAL", StringComparison.Ordinal)).ToArray();
        var totalRow = Assert.Single(rows, row => row.StartsWith("TOTAL", StringComparison.Ordinal));

        Assert.Equal(2, vehicleRows.Length);
        Assert.Contains(vehicleRows, row => row.StartsWith(vanA.RegistrationNumber, StringComparison.Ordinal));
        Assert.Contains(vehicleRows, row => row.StartsWith(vanB.RegistrationNumber, StringComparison.Ordinal));

        // AL-03: the Mode A bus is not on the file at all.
        Assert.DoesNotContain(bus.RegistrationNumber, text, StringComparison.Ordinal);

        var summed = vehicleRows.Sum(row => long.Parse(row.Split(',')[4], CultureInfo.InvariantCulture));
        var printed = long.Parse(totalRow.Split(',')[4], CultureInfo.InvariantCulture);

        Assert.Equal(60_000, summed);
        Assert.Equal(summed, printed);

        // And money is printed in rupees beside the minor units, so a bank reconciliation and a
        // platform reconciliation read the same row.
        Assert.Contains("600.00", totalRow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PDF is a real PDF: the trailer points at a cross-reference table whose every offset lands
    /// on the object it claims. An offset one byte out opens in some readers and not others.
    /// </summary>
    [Fact]
    public async Task The_pdf_is_structurally_valid_and_carries_the_breakdown()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync(name: "C060 Matara Movers");
        var van = await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var invoiceId = await FirstInvoiceAsync(harness, fleet);

        var (bytes, contentType) = await harness.DownloadAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=pdf", fleet.Bearer);

        Assert.Equal("application/pdf", contentType);

        PdfAssert.IsWellFormed(bytes);

        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains($"({van.RegistrationNumber})", text, StringComparison.Ordinal);
        Assert.Contains("(MageRide", text, StringComparison.Ordinal);
        Assert.Contains("C060 Matara Movers", text, StringComparison.Ordinal);
        Assert.Contains("300.00", text, StringComparison.Ordinal);
    }

    /// <summary>A Mode-A-only fleet's PDF says why it costs nothing rather than showing a blank table.</summary>
    [Fact]
    public async Task A_mode_a_only_invoice_says_so_on_the_document()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var invoiceId = await FirstInvoiceAsync(harness, fleet);

        var (pdf, _) = await harness.DownloadAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=pdf", fleet.Bearer);

        PdfAssert.IsWellFormed(pdf);

        Assert.Contains("Mode A vehicles are free", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        var (csv, _) = await harness.DownloadAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=csv", fleet.Bearer);

        var text = Encoding.UTF8.GetString(csv);

        Assert.Contains("TOTAL,,,0.00,0,LKR", text, StringComparison.Ordinal);
    }

    /// <summary>A settled invoice's document carries the entry it was settled by.</summary>
    [Fact]
    public async Task A_settled_invoice_prints_its_journal_entry()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        var invoiceId = await FirstInvoiceAsync(harness, fleet);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}", fleet.Bearer);

        var (csv, _) = await harness.DownloadAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=csv", fleet.Bearer);

        Assert.Contains(
            $"# journalEntryId,{detail.Invoice.JournalEntryId}",
            Encoding.UTF8.GetString(csv),
            StringComparison.Ordinal);
    }

    /// <summary>An unknown format is a 400 with the two this endpoint serves, not a default guess.</summary>
    [Fact]
    public async Task An_unknown_export_format_is_refused()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var invoiceId = await FirstInvoiceAsync(harness, fleet);

        using var response = await harness.GetAsync(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/export?format=xlsx", fleet.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);
    }

    /// <summary>Another organisation's invoice is a 404, from the query rather than from a check after it.</summary>
    [Fact]
    public async Task Another_organisations_invoice_is_not_found()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var mine = await harness.Seed.CreateFleetAsync();
        var theirs = await harness.Seed.CreateFleetAsync();

        await harness.Seed.AddVehicleAsync(theirs, mode: "B");
        await harness.Seed.AddVehicleAsync(mine, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var theirInvoice = await FirstInvoiceAsync(harness, theirs);

        using var response = await harness.GetAsync(
            $"/v1/fleets/{mine.Id}/billing/{theirInvoice}", mine.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> FirstInvoiceAsync(FleetBillingHarness harness, SeededFleet fleet)
    {
        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        return page.Items[0].InvoiceId;
    }
}
