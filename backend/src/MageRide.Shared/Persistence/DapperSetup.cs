using Dapper;
using MageRide.Shared.Persistence.TypeHandlers;

namespace MageRide.Shared.Persistence;

/// <summary>
/// One-time Dapper configuration shared by every service: snake_case column mapping and the
/// platform type handlers.
/// </summary>
/// <remarks>
/// Dapper's configuration is process-global static state, so this runs once per process and is
/// safe to call repeatedly. <c>AddMageRidePostgres</c> calls it; tests that use Dapper without the
/// DI container call it directly.
/// </remarks>
public static class DapperSetup
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary><see langword="true"/> once <see cref="Configure"/> has run in this process.</summary>
    public static bool IsConfigured
    {
        get
        {
            lock (Gate)
            {
                return _configured;
            }
        }
    }

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            // ADD §9.1 columns are snake_case; the CLR models are PascalCase. Dapper strips the
            // underscores for both property and constructor-parameter binding, so records with a
            // primary constructor map without per-type configuration.
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            SqlMapper.AddTypeHandler(new GeoPointTypeHandler());
            SqlMapper.AddTypeHandler(new NullableGeoPointTypeHandler());
            SqlMapper.AddTypeHandler(new MoneyTypeHandler());
            SqlMapper.AddTypeHandler(new NullableMoneyTypeHandler());

            // Dapper resolves DateTimeOffset from its own built-in type map before consulting
            // handlers, so the map entry has to go first or the handler is never reached and
            // Npgsql rejects any non-UTC offset outright.
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            SqlMapper.AddTypeHandler(new NullableDateTimeOffsetTypeHandler());

            SqlMapper.RemoveTypeMap(typeof(DateOnly));
            SqlMapper.RemoveTypeMap(typeof(DateOnly?));
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());

            // The wall-clock companion (Δ C062): `fares.peak_windows` keeps recurring daily windows
            // as TIME, because a surcharge that runs 22:00–05:00 every day is not an instant.
            SqlMapper.RemoveTypeMap(typeof(TimeOnly));
            SqlMapper.RemoveTypeMap(typeof(TimeOnly?));
            SqlMapper.AddTypeHandler(new TimeOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new NullableTimeOnlyTypeHandler());

            _configured = true;
        }
    }
}
