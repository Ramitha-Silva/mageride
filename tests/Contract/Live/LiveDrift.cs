namespace MageRide.Contract.Tests.Live;

/// <summary>
/// What the deployed edge gets wrong today, one entry per finding, with the component that owns the
/// fix — the same ledger discipline `Runtime/RouteDrift` applies to the in-process sweep.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, from this suite's CLAUDE.md: never soften an assertion to make it pass.</b> An entry
/// here does not excuse a failure, it *pins* it: the sweep asserts that a ledgered operation still
/// exhibits the recorded symptom, so the day somebody fixes it the suite fails and says "delete this
/// entry". The list can therefore only shrink, and a green run means "no new drift" rather than
/// "nothing is wrong".
/// </para>
/// <para>
/// Every entry below was found by the first run of this transport, 2026-08-11, against the C125
/// replica. None of them is visible to an in-process suite.
/// </para>
/// </remarks>
internal static class LiveDrift
{
    /// <summary>
    /// Operations that answer a 5xx, keyed <c>METHOD /template</c>, with the status they answer.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int Status, string Why)> ServerErrors =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            ["GET /v1/drivers/{driverId}/level"] = (500,
                "dispatch-svc: a READ that writes. GetLevelAsync is 'refresh-then-read' and the "
                + "refresh does INSERT INTO dispatch.driver_levels … ON CONFLICT DO NOTHING, which "
                + "for an unknown driver violates driver_levels_driver_id_fkey (23503) and leaves a "
                + "raw Npgsql exception as the answer. dispatch.yaml declares only 200 and 401, so "
                + "there is no 404 to return either: the contract and the service both need the "
                + "micro-change-set. OWNER: C047 (driver levels) + C007 (dispatch.yaml)."),

            ["GET /v1/drivers/{driverId}/stats"] = (500,
                "the same INSERT, reached through GetStatsAsync -> RefreshAsync. One fix covers both."),

            ["GET /v1/fleets/{fleetId}/health"] = (503,
                "fleet-health-svc is UNREACHABLE from the gateway, two ways at once. It listens on "
                + "127.0.0.1:5202 inside the hot-path container (HotPath/Program.cs passes "
                + "bindAddress \"127.0.0.1\" for all four co-located services), so no other container "
                + "can reach it on any port — and the replica's cluster address names port 5203, "
                + "while docker-compose.dev.yml's comment names 5000. Three places, three ports, none "
                + "of them right, and US-3.13 has never worked on a deployment. Same root cause as "
                + "C126's JWKS finding: a co-located service bound to loopback cannot be a cluster "
                + "destination. OWNER: C125 (the replica's compose + the HotPath host)."),
        };

    /// <summary>
    /// Operations whose 2xx body does not match the declared schema, and why the mismatch is real
    /// rather than a validator artefact.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SchemaViolations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET /v1/users/me"] =
                "`phone` is serialised as \"\" for a web-only internal account (iam.users.phone is "
                + "NULL for staff — 0101's own comment says so), and iam.yaml types it "
                + "`pattern: ^\\+947\\d{8}$` with no nullable branch. Every Admin/Fleet Portal "
                + "profile read is therefore non-conforming. Either the column's absence is `null` on "
                + "the wire and the contract declares it, or staff accounts carry a phone. "
                + "OWNER: C026/C027 (iam-svc) + C007 (iam.yaml).",

            ["GET /v1/me/bootstrap"] =
                "the same field, through `profile.phone` on the eager-fetch payload.",

            ["GET /v1/admin/audit-log"] =
                "`actorId` is REQUIRED by admin-bff.yaml and absent from every system-initiated "
                + "event. That is not a serialisation slip: migration 1305 makes audit.events "
                + "actor_id nullable in as many words — \"a system-initiated action (expiry "
                + "auto-suspend, scheduled job) has no actor\" — and GtfsAuditActions describes "
                + "GTFS_FEED_VALIDATED as \"actor-less by construction … a queued job decided it, "
                + "not a person\". The contract requires what the schema and the services both "
                + "deliberately omit, so every page of the audit log that contains one system event "
                + "is non-conforming. Found because C126's own validation events are the actor-less "
                + "rows. OWNER: C062/C065 (admin-bff) + C007 (admin-bff.yaml) — the fix is a "
                + "nullable actorId, not an invented actor.",
        };

    /// <summary>
    /// Contract documents whose service is not deployed on the target at all — passed in by the
    /// runner, which is the only thing that knows what is running.
    /// </summary>
    /// <remarks>
    /// A 503 from an operation whose service is behind a compose profile nobody enabled is not drift
    /// and must not be ledgered as though it were: the platform is answering correctly about a
    /// dependency that is absent. `contract-live-verify.sh` detects those and exports
    /// <c>MAGERIDE_LIVE_ABSENT</c>; the sweep skips them, naming the profile.
    /// </remarks>
    public static IReadOnlySet<string> AbsentDocuments =>
        (Environment.GetEnvironmentVariable("MAGERIDE_LIVE_ABSENT") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
