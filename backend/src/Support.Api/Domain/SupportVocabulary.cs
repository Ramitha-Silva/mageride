using System.Diagnostics.CodeAnalysis;

namespace MageRide.Support.Domain;

/// <summary>The three states of a ticket — <c>ck_tickets_status</c> (migration 1303) exactly.</summary>
public static class TicketStatuses
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string Resolved = "RESOLVED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Open, InProgress, Resolved,
    };

    /// <summary>Normalises a <c>?status=</c> filter, or <see langword="null"/> when it names nothing.</summary>
    public static string? TryNormalise(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var upper = status.Trim().ToUpperInvariant();

        return All.Contains(upper) ? upper : null;
    }
}

/// <summary>
/// What can happen to a ticket, as <c>ck_ticket_events_kind</c> (migration 1309) spells it.
/// </summary>
public static class TicketEventKinds
{
    public const string Opened = "opened";
    public const string Assigned = "assigned";
    public const string Responded = "responded";
    public const string Resolved = "resolved";

    /// <summary>
    /// Declared by the CHECK and written by nothing yet.
    /// </summary>
    /// <remarks>
    /// No spec gives a user or an agent a way to reopen a resolved ticket — US-16.3 ends at
    /// "mark them as resolved" — so there is no route for it and inventing one would be a decision
    /// about a screen. The value exists so that when one lands it is not a migration; raised in the
    /// C053 handoff.
    /// </remarks>
    public const string Reopened = "reopened";

    /// <summary>
    /// Kinds the ticket's own user may see.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Assigned"/> is not among them, deliberately.</b> Who inside MageRide is
    /// handling a complaint is not the complainant's business — and a complaint about a named
    /// driver, routed to a named CSR, is exactly the pairing that should not be readable by the
    /// person who filed it. The queue still needs the trail, so the row is written and the *filter*
    /// is what withholds it.
    /// </remarks>
    public static readonly IReadOnlySet<string> UserVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        Opened, Responded, Resolved, Reopened,
    };
}

/// <summary>
/// Which back-office pile a ticket belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the category, never stored.</b> `support.tickets` has a second writer —
/// subscription-svc (C047) raises the US-9.23 refund request itself, because only it can say
/// whether the driver was in fact charged on the day they are disputing — and a stored column
/// would default those rows onto the Support queue, which is the one case this routing exists
/// for. A pure function over `category` gets the same answer whoever wrote the row.
/// </para>
/// <para>
/// URD §2.3's role mapping is the source: "support tickets &amp; disputes (US-14.13) by
/// <b>Support/CSR</b>; wallet reversals … (US-14.11) by <b>Finance Officer</b>". A daily-fee refund
/// request ends in a wallet credit, so it is Finance's from the moment it is raised rather than
/// after a CSR has read it and passed it on.
/// </para>
/// </remarks>
public static class TicketQueues
{
    public const string Support = "support";
    public const string Finance = "finance";

    /// <summary>
    /// The categories that go to Finance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two entries, and both are spelled to match the services that write them:
    /// <c>RefundRequestRepository.Category</c> in subscription-svc (C047, US-9.23 — the daily-fee
    /// refund claim that ends in US-14.11's wallet reversal) and
    /// <c>SupportTicketRepository.DriverQrCategory</c> in fare-svc (C050, AL-47 — "the money went
    /// bank-to-bank and one party says it did not arrive", which that service's own comment says
    /// exists so the Finance queue can tell it from every other payment question).
    /// </para>
    /// <para>
    /// A set rather than two constants, because the next Finance category is a one-line change here
    /// and no migration. <b>The strings are duplicated across three services on purpose</b>: a shared
    /// constant would put a category vocabulary in the kernel that every bounded context would then
    /// be tempted to extend, and <c>support.tickets.category</c> deliberately carries no CHECK.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> FinanceCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "daily_fee_refund",
        "driver_qr_dispute",
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Support, Finance,
    };

    /// <summary>The queue a ticket of this category is worked from.</summary>
    public static string For(string? category) =>
        category is not null && FinanceCategories.Contains(category) ? Finance : Support;

    /// <summary>Normalises a <c>?queue=</c> filter, or <see langword="null"/> when it names nothing.</summary>
    public static string? TryNormalise(string? queue)
    {
        if (string.IsNullOrWhiteSpace(queue))
        {
            return null;
        }

        var lower = queue.Trim().ToLowerInvariant();

        return All.Contains(lower) ? lower : null;
    }
}

/// <summary>
/// The three languages the FAQ exists in (D-26, CLAUDE.md "Trilingual resources"), and the order a
/// request falls back through.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same vocabulary and the same two orders as content-svc's <c>Languages</c>
/// (C045), because the two services read the same table: <c>All</c> is Sinhala-first because AL-26
/// makes it the default the picker opens on, and <c>FallbackOrder</c> is English-first because
/// falling back is a different question — what to serve when the asked-for language is genuinely
/// absent — and English is the one every operator and CSR on this platform reads.
/// </para>
/// <para>
/// The set is closed in three places that have to agree: this vocabulary,
/// <c>ck_faq_articles_language</c> (migration 1304) and <c>LanguageCode</c> in
/// <c>backend/contracts/_shared.yaml</c>. A fourth language is a schema change, a contract change
/// and a translation project — never a config value.
/// </para>
/// </remarks>
public static class Languages
{
    public const string Sinhala = "si";
    public const string Tamil = "ta";
    public const string English = "en";

    /// <summary>Presentation order: Sinhala first and default (AL-26).</summary>
    public static readonly string[] All = [Sinhala, Tamil, English];

    /// <summary>What to try when the requested language has no row. English first.</summary>
    public static readonly string[] FallbackOrder = [English, Sinhala, Tamil];

    public static bool IsKnown([NotNullWhen(true)] string? language) =>
        language is not null && Array.IndexOf(All, language) >= 0;

    /// <summary>
    /// Normalises a <c>?lang=</c> value, or returns <see langword="null"/> when it names nothing.
    /// </summary>
    /// <remarks>
    /// Accepts the BCP 47 forms a mobile platform hands an app without being asked — <c>si-LK</c>,
    /// <c>ta_IN</c>, <c>EN</c>. A client that sent its device locale verbatim gets its language
    /// rather than English. <b>Not</b> a general BCP 47 parser: the primary subtag is matched
    /// against the three and nothing else.
    /// </remarks>
    public static string? TryNormalise(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var span = language.AsSpan().Trim();
        var separator = span.IndexOfAny('-', '_');
        var primary = separator < 0 ? span : span[..separator];

        foreach (var candidate in All)
        {
            if (primary.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The language to ask for: the requested one, or <see cref="English"/>.</summary>
    /// <remarks>
    /// <c>_shared.yaml</c>'s <c>Lang</c> parameter documents the order as "requested → the caller's
    /// profile language → <c>en</c>", and the middle step is deliberately not implemented — the same
    /// call content-svc made and for the same reason: <c>iam.users.language</c> belongs to iam-svc,
    /// and reading another bounded context's row to pick a FAQ language would put an availability
    /// dependency on it. The apps send the profile language as <c>?lang=</c>, having stored it at
    /// onboarding (AL-26).
    /// </remarks>
    public static string Resolve(string? requested) => TryNormalise(requested) ?? English;

    /// <summary>
    /// <paramref name="requested"/> followed by every other language, in fallback order.
    /// </summary>
    /// <remarks>
    /// The whole order, not just the first alternative, because "requested → English" leaves a
    /// Tamil reader with nothing at all when an article exists only in Sinhala. Every entry appears
    /// once and the requested one is always first.
    /// </remarks>
    public static string[] Preference(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);

        return [requested, .. FallbackOrder.Where(l => !string.Equals(l, requested, StringComparison.Ordinal))];
    }
}
