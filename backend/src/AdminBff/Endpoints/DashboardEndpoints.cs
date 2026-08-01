using System.Globalization;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Query;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-002 — the landing dashboard and its period filter (US-14.6, AL-38, US-24.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three routes over one read model and no second query.</b> C061 is a class library this process
/// references (see <c>Analytics/CLAUDE.md</c> for why it is not a service), so the figures on the
/// card, the figures in the JSON and the figures in the CSV all come from one
/// <c>IDashboardStatsService</c> call. "The stats endpoint returns identical numbers to a direct
/// analytics query for the same period" is therefore structural rather than a coincidence two
/// implementations happen to share.
/// </para>
/// <para>
/// <b>Gated on Analytics · Read, which is exactly URD §2.3's row.</b> Admin and Super Admin ✅,
/// Auditor and Support/CSR 👁, Finance ◐ financial, Verification Officer ➖ — so the one internal
/// role with no analytics cell gets a 403 rather than an empty dashboard, and the nav manifest drops
/// the item for the same reason.
/// </para>
/// <para>
/// <b>Reads are not audited here.</b> D-35 audits mutations; AL-39/AL-40 add <c>DOC_VIEW</c> and
/// <c>PII_READ</c> because those reads disclose a person's data. A count of yesterday's trips
/// discloses nothing about anybody, and a row per dashboard refresh would bury the rows that matter
/// under the one screen every internal user leaves open.
/// </para>
/// </remarks>
internal static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/dashboard", GetAsync)
            .WithName("getAdminDashboard")
            .WithSummary("Platform-wide metrics (US-14.6).")
            .RequireFeature(FeatureAreas.Analytics, PermissionGrant.Read);

        admin.MapGet("/dashboard/stats", GetStatsAsync)
            .WithName("getAdminDashboardStats")
            .WithSummary("Period-filtered KPIs with previous-period deltas (AL-38).")
            .RequireFeature(FeatureAreas.Analytics, PermissionGrant.Read);

        admin.MapGet("/dashboard/stats.csv", ExportStatsAsync)
            .WithName("exportAdminDashboardStats")
            .WithSummary("CSV export of exactly the filtered figures (AL-38).")
            .RequireFeature(FeatureAreas.Analytics, PermissionGrant.Read);

        return admin;
    }

    /// <remarks>
    /// The unfiltered view is <c>today</c>. C061 takes no view on which period this should be
    /// ("no spec states one") and today is the only answer that agrees with the live block beside
    /// it: an operator opening the console sees three real-time cards and five figures about the
    /// same day, rather than five figures about a month and three about this second.
    /// </remarks>
    private static async Task<Ok<AdminDashboardResponse>> GetAsync(
        IDashboardStatsService stats, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var today = await stats.GetAsync(StatsPeriods.Today, from: null, to: null, cancellationToken);

        return TypedResults.Ok(new AdminDashboardResponse(today.Kpis, today.Live));
    }

    private static async Task<Ok<DashboardStatsResponse>> GetStatsAsync(
        string? period,
        DateOnly? from,
        DateOnly? to,
        IDashboardStatsService stats,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var result = await stats.GetAsync(period, from, to, cancellationToken);

        return TypedResults.Ok(new DashboardStatsResponse(
            result.Period,
            new StatsRangeResponse(result.Range.From, result.Range.To),
            result.Kpis,
            result.DeltaVsPrev,
            result.Live));
    }

    /// <remarks>
    /// The filename carries the range, because a folder of <c>stats.csv</c> files is a folder of
    /// files nobody can tell apart — and the file's own preamble states both windows, so a
    /// percentage in it stays falsifiable after the request that produced it is gone.
    /// </remarks>
    private static async Task<IResult> ExportStatsAsync(
        string? period,
        DateOnly? from,
        DateOnly? to,
        IDashboardStatsService stats,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var resolved = await stats.GetAsync(period, from, to, cancellationToken);
        var csv = await stats.ExportCsvAsync(period, from, to, cancellationToken);

        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"mageride-stats-{resolved.Range.From:yyyyMMdd}-{resolved.Range.To:yyyyMMdd}.csv");

        return Results.File(csv, $"{MediaTypeNames.Csv}; charset=utf-8", name);
    }

    private static class MediaTypeNames
    {
        public const string Csv = "text/csv";
    }
}
