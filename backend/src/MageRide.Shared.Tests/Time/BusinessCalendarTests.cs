using MageRide.Shared.Time;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Shared.Tests.Time;

/// <summary>Business dates settle in Asia/Colombo, not UTC (D-38, D-13).</summary>
public sealed class BusinessCalendarTests
{
    [Fact]
    public void The_colombo_zone_resolves_on_this_host()
    {
        // InvariantGlobalization is off precisely so this works (backend/Directory.Build.props).
        Assert.Equal("Asia/Colombo", BusinessCalendar.TimeZone.Id);
    }

    /// <summary>
    /// The bug this type exists to prevent: 20:00 UTC is already the next day in Colombo, so a
    /// daily fee keyed on the UTC date would charge twice for one local day.
    /// </summary>
    [Fact]
    public void An_instant_after_1830_utc_belongs_to_the_next_colombo_day()
    {
        var justBefore = new DateTimeOffset(2026, 7, 27, 18, 29, 0, TimeSpan.Zero);
        var justAfter = new DateTimeOffset(2026, 7, 27, 18, 31, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 7, 27), BusinessCalendar.BusinessDate(justBefore));
        Assert.Equal(new DateOnly(2026, 7, 28), BusinessCalendar.BusinessDate(justAfter));
    }

    [Fact]
    public void Midnight_utc_is_still_the_same_colombo_date_at_0530()
    {
        var midnightUtc = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 7, 27), BusinessCalendar.BusinessDate(midnightUtc));
        Assert.Equal(5, BusinessCalendar.ToLocal(midnightUtc).Hour);
        Assert.Equal(30, BusinessCalendar.ToLocal(midnightUtc).Minute);
    }

    [Fact]
    public void A_day_range_is_half_open_and_exactly_24_hours()
    {
        var (start, end) = BusinessCalendar.DayRange(new DateOnly(2026, 7, 27));

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 18, 30, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 18, 30, 0, TimeSpan.Zero), end);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
    }

    [Fact]
    public void Consecutive_day_ranges_abut_without_a_gap_or_overlap()
    {
        var first = BusinessCalendar.DayRange(new DateOnly(2026, 7, 27));
        var second = BusinessCalendar.DayRange(new DateOnly(2026, 7, 28));

        Assert.Equal(first.End, second.Start);
    }

    [Fact]
    public void A_month_range_covers_the_colombo_calendar_month()
    {
        var (start, end) = BusinessCalendar.MonthRange(2026, 7);

        Assert.Equal(new DateTimeOffset(2026, 6, 30, 18, 30, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 18, 30, 0, TimeSpan.Zero), end);
    }

    /// <summary>
    /// Sri Lanka ran on +06:00 between 1996 and 2006. Resolving from the tz database rather than
    /// hard-coding +05:30 keeps historical instants on the right local date.
    /// </summary>
    [Fact]
    public void Historical_offsets_come_from_the_tz_database()
    {
        var instant = new DateTimeOffset(2000, 1, 1, 18, 45, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(6), BusinessCalendar.TimeZone.GetUtcOffset(instant));
        Assert.Equal(new DateOnly(2000, 1, 2), BusinessCalendar.BusinessDate(instant));
    }

    [Fact]
    public void Today_follows_the_supplied_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 7, 28), BusinessCalendar.Today(clock));
    }

    [Fact]
    public void Stamp_returns_the_date_and_the_utc_instant_add_9_1_stores_together()
    {
        var instant = new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);
        var (date, tzAt) = BusinessCalendar.Stamp(instant);

        Assert.Equal(new DateOnly(2026, 7, 28), date);
        Assert.Equal(instant, tzAt);
        Assert.Equal(TimeSpan.Zero, tzAt.Offset);
    }

    [Fact]
    public void Same_business_date_compares_local_days_not_utc_days()
    {
        var eveningUtc = new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);
        var nextMorningUtc = new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);

        Assert.True(BusinessCalendar.IsSameBusinessDate(eveningUtc, nextMorningUtc));
    }

    [Fact]
    public void The_date_key_matches_the_redis_pattern()
    {
        Assert.Equal("2026-07-27", BusinessCalendar.DateKey(new DateOnly(2026, 7, 27)));
    }
}
