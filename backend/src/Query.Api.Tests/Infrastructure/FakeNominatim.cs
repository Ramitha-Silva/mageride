using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MageRide.Query.Tests.Infrastructure;

/// <summary>
/// A stand-in Nominatim on a real socket, answering the two routes query-svc calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a mocked <c>IGeocoder</c>, on purpose.</b> What is worth asserting about geocoding is the
/// part between the two services: the query string this service builds, the <c>format=jsonv2</c> shape
/// it parses back, the mapping of OSM's uneven address tagging onto AL-26's address lines, and the
/// cache that keeps a second identical search off the wire. A substituted interface would skip every
/// one of those and prove only that a fake returns what it was told to.
/// </para>
/// <para>
/// A real Nominatim needs an 8 GB Sri Lanka extract (ADD §6) and is not something a unit-test
/// container can hold. This serves the same JSON over the same protocol.
/// </para>
/// </remarks>
internal sealed class FakeNominatim : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _requests = [];
    private readonly Lock _gate = new();

    private FakeNominatim(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>What <c>Query:NominatimBaseUrl</c> should be set to.</summary>
    public string BaseUrl { get; }

    /// <summary>Every path+query this instance has been asked for, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Places the forward search returns. Replaceable per test.</summary>
    public List<GeocodedFixture> Places { get; } =
    [
        new(6.9344, 79.8428, "Colombo Fort Railway Station, Colombo, Western Province", "Olcott Mawatha", "Colombo"),
    ];

    /// <summary>What reverse geocoding answers, or <see langword="null"/> for a 404.</summary>
    public GeocodedFixture? ReverseResult { get; set; } =
        new(6.9271, 79.8612, "Galle Face Green, Colombo 03", "Galle Road", "Colombo");

    public static async Task<FakeNominatim> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        FakeNominatim? instance = null;

        app.MapGet("/search", (HttpContext context) =>
        {
            instance!.Record(context);

            return Results.Json(instance.Places.Select(place => place.ToSearchResult()).ToArray());
        });

        app.MapGet("/reverse", (HttpContext context) =>
        {
            instance!.Record(context);

            // A real Nominatim answers a coordinate it cannot place — the middle of the sea — with a
            // 404 and an error body, which is a real answer and not a failure. The client under test
            // has to tell those apart.
            return instance.ReverseResult is { } result
                ? Results.Json(result.ToSearchResult())
                : Results.Json(new { error = "Unable to geocode" }, statusCode: 404);
        });

        await app.StartAsync();

        var baseUrl = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        instance = new FakeNominatim(app, baseUrl);

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Record(HttpContext context)
    {
        lock (_gate)
        {
            _requests.Add(context.Request.Path + context.Request.QueryString);
        }
    }

    /// <summary>One Nominatim result, in the fields <c>format=jsonv2&amp;addressdetails=1</c> carries.</summary>
    internal sealed record GeocodedFixture(
        double Lat, double Lng, string DisplayName, string? Road, string? City)
    {
        internal GeoPoint Point => new(Lat, Lng);

        internal object ToSearchResult() => new
        {
            lat = Lat.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            lon = Lng.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            display_name = DisplayName,
            name = DisplayName.Split(',')[0],
            address = new { road = Road, city = City },
        };
    }
}
