using System.Reflection;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using MageRide.Shared.Resilience;

namespace MageRide.Shared.Tests.Conventions;

/// <summary>
/// Fences from CLAUDE.md and the C002 prompt, enforced as tests rather than as review notes.
/// </summary>
public sealed class StackFenceTests
{
    /// <summary>
    /// AL-53: Dapper over Npgsql, no EF Core anywhere. The C002 DoD greps for it; this catches a
    /// transitive reference too, which a grep over source would miss.
    /// </summary>
    [Fact]
    public void The_shared_kernel_references_no_entity_framework_assembly()
    {
        // Assembled from fragments so this file does not itself match the DoD grep for
        // EF Core over backend/src.
        var banned = "Entity" + "FrameworkCore";

        var offenders = typeof(MageRideServiceDefaults).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.Contains(banned, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// MassTransit is not a general bus (C002 fence): only ride-svc uses it, and only where the
    /// ADD names it. The shared kernel must not drag it into every service.
    /// </summary>
    [Fact]
    public void The_shared_kernel_does_not_reference_masstransit()
    {
        var offenders = typeof(MageRideServiceDefaults).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("MassTransit", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// "No domain rules and no endpoints live here" (C002 fence). Route-mapping belongs to the
    /// services; the kernel's only exceptions are the infrastructure surfaces D7' §5.1 and §12
    /// require every service to expose.
    /// </summary>
    [Fact]
    public void The_shared_kernel_maps_no_endpoints_beyond_health_and_metrics()
    {
        var mappers = typeof(MageRideServiceDefaults).Assembly
            .GetTypes()
            .Where(t => t.IsSealed && t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Map", StringComparison.Ordinal))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["HealthEndpointExtensions.MapMageRideHealthChecks", "OpenTelemetryExtensions.MapMageRideMetrics"],
            mappers);
    }

    [Fact]
    public void Json_is_camel_case_with_string_enums()
    {
        Assert.Equal(System.Text.Json.JsonNamingPolicy.CamelCase, MageRideJson.Options.PropertyNamingPolicy);
        Assert.False(MageRideJson.Options.WriteIndented);
        Assert.True(MageRideJson.Options.IsReadOnly);
    }

    /// <summary>The Redis key patterns must match ADD §9.4 exactly, or two services miss each other.</summary>
    [Fact]
    public void Redis_keys_match_the_add_9_4_patterns()
    {
        var vehicleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var driverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rideId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        Assert.Equal("geo:live", RedisKeys.GeoLive);
        Assert.Equal("veh:meta:11111111-1111-1111-1111-111111111111", RedisKeys.VehicleMeta(vehicleId));
        Assert.Equal("cell:8a2a1072b59ffff", RedisKeys.Cell("8a2a1072b59ffff"));
        Assert.Equal("trip:active:11111111-1111-1111-1111-111111111111", RedisKeys.ActiveTrip(vehicleId));
        Assert.Equal("imei:359586015829435", RedisKeys.Imei("359586015829435"));
        Assert.Equal("rate:11111111-1111-1111-1111-111111111111", RedisKeys.VehicleRateLimit(vehicleId));
        Assert.Equal("geo:drivers:available:three_wheeler:85283473", RedisKeys.AvailableDrivers("three_wheeler", "85283473"));
        Assert.Equal("driver:availability:22222222-2222-2222-2222-222222222222", RedisKeys.DriverAvailability(driverId));
        Assert.Equal("driver:directional:22222222-2222-2222-2222-222222222222", RedisKeys.DriverDirectional(driverId));
        Assert.Equal("driver:directional:uses:22222222-2222-2222-2222-222222222222:2026-07-27",
            RedisKeys.DriverDirectionalUses(driverId, new DateOnly(2026, 7, 27)));
        Assert.Equal("offer:33333333-3333-3333-3333-333333333333", RedisKeys.Offer(rideId));
        Assert.Equal("lock:driver-offer:22222222-2222-2222-2222-222222222222", RedisKeys.DriverOfferLock(driverId));
        Assert.Equal("lock:ride:33333333-3333-3333-3333-333333333333", RedisKeys.RideLock(rideId));
        Assert.Equal("refresh:abc123", RedisKeys.RefreshToken("abc123"));
        Assert.Equal("ride_outbox", RedisKeys.OutboxNotifyChannel);
    }

    /// <summary>The numbers in D6' §8.3.</summary>
    [Fact]
    public void Resilience_defaults_are_the_D6_8_3_budgets()
    {
        var options = new ResilienceOptions();

        Assert.Equal(2, options.MaxRetryAttempts); // 3 attempts in total
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), options.MaxDelay);
        Assert.Equal(0.25, options.JitterFactor);
        Assert.Equal(TimeSpan.FromSeconds(30), options.BreakerSamplingDuration);
        Assert.Equal(5, options.BreakerMinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(15), options.BreakerBreakDuration);

        Assert.Equal(TimeSpan.FromSeconds(15), MageRideTimeouts.Api);
        Assert.Equal(TimeSpan.FromSeconds(90), MageRideTimeouts.PaymentProvider);
        Assert.Equal(TimeSpan.FromSeconds(30), MageRideTimeouts.Ocr);
        Assert.Equal(TimeSpan.FromMilliseconds(500), MageRideTimeouts.KafkaPoll);
    }

    [Fact]
    public void Retry_backoff_is_exponential_and_capped_with_25_percent_jitter()
    {
        var options = new ResilienceOptions();

        foreach (var (attempt, expected) in new[] { (0, 100.0), (1, 200.0), (2, 400.0) })
        {
            for (var i = 0; i < 50; i++)
            {
                var delay = MageRideResilience.Jitter(options, attempt).TotalMilliseconds;
                Assert.InRange(delay, expected * 0.75, expected * 1.25);
            }
        }

        // Past the cap, the ceiling holds (plus the jitter band).
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(MageRideResilience.Jitter(options, 10).TotalMilliseconds, 1500, 2500);
        }
    }

    [Theory]
    [InlineData("1.4.2", 1, 4, 2, 0)]
    [InlineData("1.4.2+318", 1, 4, 2, 318)]
    [InlineData("2.0", 2, 0, 0, 0)]
    [InlineData("3", 3, 0, 0, 0)]
    public void App_version_parses_the_D_31_header(string raw, int major, int minor, int patch, int build)
    {
        Assert.True(AppVersion.TryParse(raw, out var version));
        Assert.Equal(new AppVersion(major, minor, patch, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.4.2.9")]
    [InlineData("1.x.2")]
    [InlineData("v1.4.2")]
    [InlineData("1.4.2+")]
    public void A_malformed_app_version_does_not_parse(string raw)
    {
        Assert.False(AppVersion.TryParse(raw, out _));
    }

    [Fact]
    public void App_versions_order_correctly()
    {
        Assert.True(AppVersion.Parse("1.4.2") < AppVersion.Parse("1.10.0"));
        Assert.True(AppVersion.Parse("1.4.2") < AppVersion.Parse("1.4.3"));
        Assert.True(AppVersion.Parse("1.4.2+1") < AppVersion.Parse("1.4.2+2"));
        Assert.True(AppVersion.Parse("2.0.0") > AppVersion.Parse("1.99.99"));
        Assert.Equal("1.4.2", AppVersion.Parse("1.4.2").ToString());
        Assert.Equal("1.4.2+7", AppVersion.Parse("1.4.2+7").ToString());
    }
}
