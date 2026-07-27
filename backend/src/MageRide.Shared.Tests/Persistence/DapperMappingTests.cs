using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Tests.Infrastructure;

namespace MageRide.Shared.Tests.Persistence;

/// <summary>
/// snake_case mapping and the platform type handlers against a real PostGIS-enabled Postgres 16
/// (D3' §0 "Data access", ADD §9.1/§9.5).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DapperMappingTests(PostgresFixture postgres)
{
    /// <summary>A record shaped like a row from ADD §9.1 — snake_case columns, PascalCase members.</summary>
    private sealed record RideRow(
        Guid RideId,
        string PaymentMethod,
        Money FareMinor,
        Money? DiscountMinor,
        GeoPoint PickupGeo,
        GeoPoint? DropoffGeo,
        DateTimeOffset RequestedAt,
        DateTimeOffset? CompletedAt,
        DateOnly BusinessDate);

    private const string Ddl =
        """
        CREATE EXTENSION IF NOT EXISTS postgis;
        CREATE SCHEMA IF NOT EXISTS mapping;
        CREATE TABLE IF NOT EXISTS mapping.rides (
          ride_id        UUID PRIMARY KEY,
          payment_method TEXT NOT NULL,
          fare_minor     BIGINT NOT NULL,
          discount_minor BIGINT,
          pickup_geo     geography(Point,4326) NOT NULL,
          dropoff_geo    geography(Point,4326),
          requested_at   TIMESTAMPTZ NOT NULL,
          completed_at   TIMESTAMPTZ,
          business_date  DATE NOT NULL);
        """;

    private async Task<INpgsqlConnectionFactory> PrepareAsync()
    {
        var factory = TestHosts.ConnectionFactory(postgres.ConnectionString);

        await using var connection = await factory.OpenAsync();
        await connection.ExecuteAsync(Ddl);

        return factory;
    }

    [Fact]
    public async Task A_row_round_trips_through_snake_case_columns_and_the_type_handlers()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        await using var connection = await factory.OpenAsync();

        var expected = new RideRow(
            RideId: Guid.NewGuid(),
            PaymentMethod: "cash",
            FareMinor: Money.FromMinor(48000),
            DiscountMinor: Money.FromMinor(5000),
            PickupGeo: new GeoPoint(6.9271, 79.8612),
            DropoffGeo: new GeoPoint(6.9500, 79.9000),
            RequestedAt: new DateTimeOffset(2026, 7, 27, 10, 15, 30, TimeSpan.Zero),
            CompletedAt: new DateTimeOffset(2026, 7, 27, 10, 42, 0, TimeSpan.Zero),
            BusinessDate: new DateOnly(2026, 7, 27));

        await connection.ExecuteAsync(
            """
            INSERT INTO mapping.rides
              (ride_id, payment_method, fare_minor, discount_minor, pickup_geo, dropoff_geo,
               requested_at, completed_at, business_date)
            VALUES
              (@RideId, @PaymentMethod, @FareMinor, @DiscountMinor, @PickupGeo, @DropoffGeo,
               @RequestedAt, @CompletedAt, @BusinessDate);
            """,
            expected);

        var actual = await connection.QuerySingleAsync<RideRow>(
            "SELECT * FROM mapping.rides WHERE ride_id = @RideId", new { expected.RideId });

        Assert.Equal(expected.PaymentMethod, actual.PaymentMethod);
        Assert.Equal(expected.FareMinor, actual.FareMinor);
        Assert.Equal(expected.DiscountMinor, actual.DiscountMinor);
        Assert.Equal(expected.RequestedAt, actual.RequestedAt);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.BusinessDate, actual.BusinessDate);

        // geography stores float8 coordinates; compare to sub-metre precision.
        Assert.Equal(expected.PickupGeo.Latitude, actual.PickupGeo.Latitude, 6);
        Assert.Equal(expected.PickupGeo.Longitude, actual.PickupGeo.Longitude, 6);
        Assert.Equal(expected.DropoffGeo!.Value.Latitude, actual.DropoffGeo!.Value.Latitude, 6);
    }

    [Fact]
    public async Task Nulls_map_to_nullable_members()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        await using var connection = await factory.OpenAsync();

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO mapping.rides
              (ride_id, payment_method, fare_minor, discount_minor, pickup_geo, dropoff_geo,
               requested_at, completed_at, business_date)
            VALUES
              (@RideId, 'cash', @Fare, @Discount, @Pickup, @Dropoff, now(), @Completed, current_date);
            """,
            new
            {
                RideId = id,
                Fare = Money.FromMinor(1000),
                Discount = (Money?)null,
                Pickup = new GeoPoint(6.9271, 79.8612),
                Dropoff = (GeoPoint?)null,
                Completed = (DateTimeOffset?)null,
            });

        var row = await connection.QuerySingleAsync<RideRow>(
            "SELECT * FROM mapping.rides WHERE ride_id = @id", new { id });

        Assert.Null(row.DiscountMinor);
        Assert.Null(row.DropoffGeo);
        Assert.Null(row.CompletedAt);
    }

    /// <summary>
    /// PostGIS stores (x=longitude, y=latitude) while the payloads are lat-first; getting this
    /// backwards puts every Colombo pickup in the Indian Ocean off Somalia.
    /// </summary>
    [Fact]
    public async Task Latitude_and_longitude_are_not_transposed()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        await using var connection = await factory.OpenAsync();

        var (lat, lng) = await connection.QuerySingleAsync<(double Lat, double Lng)>(
            "SELECT ST_Y(@p::geometry) AS lat, ST_X(@p::geometry) AS lng",
            new { p = new GeoPoint(6.9271, 79.8612) });

        Assert.Equal(6.9271, lat, 6);
        Assert.Equal(79.8612, lng, 6);
    }

    /// <summary>
    /// Npgsql refuses a non-zero-offset DateTimeOffset on a timestamptz parameter; the handler
    /// normalises to UTC so callers cannot trip over it.
    /// </summary>
    [Fact]
    public async Task A_non_utc_offset_is_normalised_rather_than_rejected()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        await using var connection = await factory.OpenAsync();

        var colomboLocal = new DateTimeOffset(2026, 7, 27, 15, 45, 30, TimeSpan.FromMinutes(330));

        var stored = await connection.QuerySingleAsync<DateTimeOffset>(
            "SELECT @ts::timestamptz", new { ts = colomboLocal });

        Assert.Equal(colomboLocal.ToUniversalTime(), stored);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
    }

    [Fact]
    public async Task Money_refuses_to_store_a_currency_other_than_lkr()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        await using var connection = await factory.OpenAsync();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            connection.QuerySingleAsync<long>("SELECT @amount::bigint", new { amount = Money.FromMinor(100, "USD") }));
    }

    [Fact]
    public async Task A_unit_of_work_rolls_back_when_it_is_not_committed()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();
        var id = Guid.NewGuid();

        await using (var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync())
        {
            await uow.Connection.ExecuteAsync(
                """
                INSERT INTO mapping.rides
                  (ride_id, payment_method, fare_minor, pickup_geo, requested_at, business_date)
                VALUES (@id, 'cash', 100, ST_MakePoint(79.8612, 6.9271)::geography, now(), current_date);
                """,
                new { id },
                uow.Transaction);
            // Deliberately no commit.
        }

        await using var connection = await factory.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM mapping.rides WHERE ride_id = @id", new { id }));
    }

    [Fact]
    public async Task A_committed_unit_of_work_cannot_be_committed_twice()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var factory = await PrepareAsync();

        await using var uow = await new NpgsqlUnitOfWorkFactory(factory).BeginAsync();
        await uow.CommitAsync();

        Assert.True(uow.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CommitAsync());
    }

    [Fact]
    public void Geo_point_rejects_out_of_range_coordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(91, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(0, 181));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(double.NaN, 0));
    }

    [Fact]
    public void Dapper_is_configured_for_snake_case()
    {
        DapperSetup.Configure();

        Assert.True(DapperSetup.IsConfigured);
        Assert.True(DefaultTypeMap.MatchNamesWithUnderscores);
    }
}
