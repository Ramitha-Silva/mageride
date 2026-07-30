namespace MageRide.FleetHealth.Rollups;

/// <summary>
/// The bucket arithmetic TimescaleDB's <c>time_bucket</c> performs, in .NET.
/// </summary>
/// <remarks>
/// <para>
/// The alert worker has to name the bucket it is evaluating — in a query predicate
/// (<c>WHERE bucket = …</c>), in an alert row, and in the API response — so the boundary has to be
/// computed here as well as in Postgres, and the two must agree exactly or the predicate matches no
/// row and every fleet reads as a total outage.
/// </para>
/// <para>
/// <b>Flooring on Unix seconds is safe for every width that divides a day evenly</b>, which the
/// 5-minute bucket does. TimescaleDB's origin for a sub-daily <c>time_bucket</c> has moved between
/// versions (the Unix epoch, 2000-01-01, 2000-01-03), and the gaps between all three are whole numbers
/// of days — so any width dividing 86 400 lands on the same boundaries whichever origin is in force.
/// <c>BucketArithmeticMatchesPostgres</c> asserts the agreement against a real server rather than
/// trusting that reasoning.
/// </para>
/// </remarks>
internal static class TimeBuckets
{
    private const int SecondsPerDay = 86_400;

    /// <summary>The start of the <paramref name="width"/>-wide bucket containing <paramref name="instant"/>.</summary>
    public static DateTimeOffset Start(DateTimeOffset instant, TimeSpan width)
    {
        var seconds = (long)width.TotalSeconds;

        if (seconds <= 0 || SecondsPerDay % seconds != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "A bucket width must be a whole number of seconds that divides a day evenly, or the " +
                "boundaries computed here and by TimescaleDB's time_bucket can disagree.");
        }

        // Floor, not truncate-toward-zero: DateTimeOffset.ToUnixTimeSeconds already floors, and every
        // instant this is called with is after 1970 anyway.
        var unix = instant.ToUnixTimeSeconds();

        return DateTimeOffset.FromUnixTimeSeconds(unix - Math.Abs(unix % seconds));
    }

    /// <summary>
    /// The start of the most recent bucket that has finished, relative to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// The bucket containing <paramref name="now"/> is still being written, so evaluating it would read
    /// a fraction of a window's samples and report most of the fleet as offline. The alert is always
    /// about a closed window.
    /// </remarks>
    public static DateTimeOffset LastClosedStart(DateTimeOffset now, TimeSpan width) =>
        Start(now, width) - width;
}
