using System.Reflection;
using System.Text.RegularExpressions;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Persistence;

namespace MageRide.Analytics.Tests.Unit;

/// <summary>
/// C061's fence, asserted rather than reviewed: <b>this component writes one table.</b>
/// </summary>
/// <remarks>
/// <para>
/// The prompt's fence is "rollups are derived from events/read replicas; this service never writes
/// to an operational schema". The read model touches six tables belonging to five other bounded
/// contexts, so the fence is exactly the property a reviewer is least likely to catch being broken
/// — an <c>UPDATE registry.vehicles</c> added to a rollup query would pass every other test in this
/// suite.
/// </para>
/// <para>
/// <b>What this proves and what it does not.</b> It reflects over every SQL constant declared in the
/// component's repositories and checks that every data-modifying keyword in them targets
/// <c>analytics.</c>. It cannot prove there is no SQL elsewhere — but there is none to find: every
/// <c>CommandDefinition</c> in the assembly takes one of these constants, which is why they are
/// constants.
/// </para>
/// </remarks>
public sealed class FenceTests
{
    /// <summary>
    /// Data-modifying keywords. <c>ON CONFLICT … DO UPDATE</c> is neutralised first: it is a clause
    /// of an <c>INSERT</c> already accounted for, not a second statement.
    /// </summary>
    private static readonly Regex WriteKeyword =
        new(@"\b(insert\s+into|update|delete\s+from|truncate)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    /// <summary>The same keywords with the schema-qualified table they target.</summary>
    private static readonly Regex WriteTarget = new(
        @"\b(?:insert\s+into|update|delete\s+from|truncate)\s+(?<table>[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    /// <summary>Every declared statement that modifies data, by the field that declares it.</summary>
    private static IReadOnlyList<(string Name, string Sql)> WritingSql() =>
        [.. DeclaredSql().Where(entry => WriteKeyword.IsMatch(entry.Sql))];

    public static TheoryData<string, string> WritingStatements()
    {
        var data = new TheoryData<string, string>();

        foreach (var (name, sql) in WritingSql())
        {
            data.Add(name, sql);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WritingStatements))]
    public void Every_write_in_this_component_targets_analytics(string name, string sql)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));

        var normalised = sql.Replace("DO UPDATE", "DO_CONFLICT_UPDATE", StringComparison.OrdinalIgnoreCase);

        var keywords = WriteKeyword.Matches(normalised).Count;
        var targets = WriteTarget.Matches(normalised);

        // Equal counts is what stops an unqualified `INSERT INTO daily_metrics` slipping past the
        // schema check by simply not matching it.
        Assert.Equal(keywords, targets.Count);

        foreach (Match target in targets)
        {
            Assert.StartsWith("analytics.", target.Groups["table"].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The suite would be vacuous if reflection found nothing. The component has exactly one
    /// statement that writes.
    /// </summary>
    [Fact]
    public void There_is_exactly_one_writing_statement_in_the_component()
    {
        Assert.Single(WritingSql());
    }

    /// <summary>
    /// No SQL in this component derives a business date. Every Colombo boundary is computed by
    /// <c>BusinessCalendar</c> and passed in as a bound, so there is one implementation of D-38 and
    /// it is the one that knows Sri Lanka has changed offset.
    /// </summary>
    /// <remarks>
    /// <c>billing.daily_fee_charges.fee_date</c> is compared to <c>@MetricDate</c> — a <c>DATE</c>
    /// against a <c>DATE</c>, both already Asia/Colombo business dates — which is the opposite of
    /// deriving one.
    /// </remarks>
    [Fact]
    public void No_query_derives_a_business_date_in_sql()
    {
        foreach (var sql in AllSql())
        {
            Assert.DoesNotContain("AT TIME ZONE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("date_trunc", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("::date", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// No query calls <c>now()</c>. Every instant a decision is made against — the presence freshness
    /// cutoff, <c>refreshed_at</c>, the metric day itself — comes from the service's
    /// <see cref="TimeProvider"/>, so one clock decides and a test can state where a boundary falls.
    /// </summary>
    [Fact]
    public void No_query_reads_the_database_clock()
    {
        foreach (var sql in AllSql())
        {
            Assert.DoesNotContain("now()", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current_timestamp", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current_date", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The literals the rollup and the live counters match on are the vocabulary constants, not
    /// hand-typed strings in a query.
    /// </summary>
    [Fact]
    public void The_state_literals_are_parameters_not_inlined_strings()
    {
        foreach (var sql in AllSql())
        {
            Assert.DoesNotContain($"'{AnalyticsVocabulary.RideCompleted}'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain($"'{AnalyticsVocabulary.DailyFeePaid}'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain($"'{AnalyticsVocabulary.PresenceOffline}'", sql, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> AllSql() => DeclaredSql().Select(entry => entry.Sql);

    /// <summary>
    /// Every SQL statement the component declares, found by reflection over its <c>const string</c>
    /// fields. The SQL is held in constants precisely so this is possible.
    /// </summary>
    private static IReadOnlyList<(string Name, string Sql)> DeclaredSql()
    {
        var found = new List<(string, string)>();

        foreach (var type in typeof(DailyMetricsRepository).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(string)
                    && field.IsLiteral
                    && field.GetRawConstantValue() is string value
                    && value.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(($"{type.Name}.{field.Name}", value));
                }
            }
        }

        Assert.NotEmpty(found);

        return found;
    }
}
