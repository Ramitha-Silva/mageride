using System.Text.Json;
using Dapper;
using MageRide.Content.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Content.Persistence;

/// <summary>One launch city, as <c>config.operating_cities</c> holds it (D4' §17b, AL-27).</summary>
internal sealed record OperatingCityRow(
    string Code,
    string NameEn,
    string NameSi,
    string NameTa,
    double CentroidLat,
    double CentroidLng,
    int SortOrder);

/// <summary>One carousel slide (AL-28, migration 1307).</summary>
internal sealed record OnboardingSlideRow(
    int Slot, string IllustrationRef, TrilingualText Title, TrilingualText Body);

/// <summary>
/// The two public reference datasets that feed the first-run screen: the launch cities and the
/// feature carousel.
/// </summary>
/// <remarks>
/// One repository because they answer one screen (SCR-DA/DI-002, SCR-PA/PI-002) and are cached and
/// invalidated the same way. Neither read is scoped to a caller — there is no caller yet, which is
/// what makes both endpoints public.
/// </remarks>
internal interface IReferenceDataRepository
{
    /// <summary>Active operating cities, in <c>sort_order</c>.</summary>
    Task<IReadOnlyList<OperatingCityRow>> ReadActiveCitiesAsync(CancellationToken cancellationToken);

    /// <summary>Active slides for one audience, in pager order.</summary>
    Task<IReadOnlyList<OnboardingSlideRow>> ReadSlidesAsync(
        string audience, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReferenceDataRepository"/>
internal sealed class ReferenceDataRepository(INpgsqlConnectionFactory connections) : IReferenceDataRepository
{
    /// <remarks>
    /// <para>
    /// <b><c>WHERE is_active</c> is the second fence and it is inside the query.</b> D3' says "active
    /// rows only" and D4' §17b says an admin toggling <c>is_active</c> takes a city out of the app
    /// with no release; a filter applied to the result of an unfiltered read would be one refactor
    /// away from serving a city the platform does not operate in, and the passenger who picked it
    /// would get an empty map and a `+94` number nobody answers.
    /// </para>
    /// <para>
    /// <b><c>ORDER BY sort_order, code</c> — the tie-break is load-bearing, not tidiness.</b> The
    /// column defaults to 0, so a newly inserted city ties with every other unranked one and
    /// Postgres is free to return ties in any order. The ETag is a digest of the payload, so an
    /// unstable order would change it on every read and defeat the caching this endpoint exists to
    /// support.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<OperatingCityRow>> ReadActiveCitiesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<OperatingCityRow>(
            new CommandDefinition(
                """
                SELECT code, name_en, name_si, name_ta, centroid_lat, centroid_lng, sort_order
                  FROM config.operating_cities
                 WHERE is_active
                 ORDER BY sort_order, code;
                """,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<OnboardingSlideRow>> ReadSlidesAsync(
        string audience, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // The two JSONB columns come back as text and are parsed here rather than through a Dapper
        // type handler: the handler would have to be registered for a dictionary type used by two
        // columns in one table, and `TrilingualText.FromStored` is where the "all three or throw"
        // rule lives.
        var rows = await connection.QueryAsync<(int Slot, string IllustrationRef, string Title, string Body)>(
            new CommandDefinition(
                """
                SELECT slot, illustration_ref, title_by_lang::text AS title, body_by_lang::text AS body
                  FROM content.onboarding_slides
                 WHERE audience = @Audience AND is_active
                 ORDER BY slot;
                """,
                new { Audience = audience },
                cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new OnboardingSlideRow(
                row.Slot,
                row.IllustrationRef,
                TrilingualText.FromStored(Parse(row.Title), $"onboarding slide {audience}/{row.Slot} title"),
                TrilingualText.FromStored(Parse(row.Body), $"onboarding slide {audience}/{row.Slot} body"))),
        ];
    }

    internal static Dictionary<string, string?>? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
}
