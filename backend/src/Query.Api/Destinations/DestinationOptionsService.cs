using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MageRide.Query.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Destinations;

/// <summary>Whether an option is public transport or a private hire.</summary>
public static class TransportOptionKinds
{
    /// <summary>A Mode C tier — price only before a match (AL-19, BR-23.3).</summary>
    public const string Private = "private";

    /// <summary>A Mode A route from the GTFS feed — bus or train (US-7.15).</summary>
    public const string Public = "public";
}

/// <summary>One way of making a journey (D3' <c>TransportOption</c>).</summary>
/// <param name="Kind"><see cref="TransportOptionKinds"/>.</param>
/// <param name="Label">What the tier or route is called.</param>
/// <param name="VehicleType">Canonical type (AL-09), for a private tier.</param>
/// <param name="RouteNumber">Route short name, for a public option.</param>
/// <param name="EstimatedFareMinor">Upfront price, minor units.</param>
/// <param name="Currency">Always LKR.</param>
/// <param name="Transfers">0 for a direct public route (AL-18); null for a private tier.</param>
public sealed record TransportOption(
    string Kind,
    string Label,
    string? VehicleType,
    string? RouteNumber,
    long? EstimatedFareMinor,
    string? Currency,
    int? Transfers);

/// <summary>Every way of reaching a destination (US-7.15).</summary>
public interface IDestinationOptionsService
{
    Task<IReadOnlyList<TransportOption>> OptionsAsync(
        GeoPoint from, GeoPoint to, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IDestinationOptionsService" path="/summary"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>This service aggregates and computes nothing.</b> The public half is GTFS route matching, which
/// is transit-svc's (C061, AL-18) — a route is "direct" when one route's stop sequence covers a halt
/// near the origin before a halt near the destination inside an admin halt-radius, and that rule lives
/// with the feed it reads. The private half is a Mode C fare, which is fare-svc's tariff, its peak and
/// night windows and its rounding (D5' §1.3, where "banker's rounding is the definition"). Either
/// computed here would be a second opinion about somebody else's number, and the visible symptom is a
/// price on the options screen that differs from the price on the confirm screen.
/// </para>
/// <para>
/// <b>Private tiers carry no ETA and no distance, structurally.</b> AL-19/BR-23.3: before dispatch a
/// Mode C tier exposes the upfront price only, because no driver has been matched and "4 minutes away"
/// would be about a vehicle nobody has reserved. <see cref="TransportOption"/> has an ETA field
/// because a public option can have one; a private option is constructed without it, in one place.
/// </para>
/// <para>
/// <b>A missing downstream removes its half of the answer and says which.</b> If transit-svc is not
/// configured the passenger sees the private tiers, which is exactly C061's own documented degradation
/// ("live buses + private tiers shown, route matching hidden"). If <em>neither</em> is configured there
/// is no answer at all to give and the endpoint returns <c>503</c> rather than an empty list — an empty
/// options screen reads as "there is no way to get there".
/// </para>
/// </remarks>
public sealed class DestinationOptionsService(
    IHttpClientFactory clients,
    IOptions<QueryOptions> options,
    ILogger<DestinationOptionsService> logger) : IDestinationOptionsService
{
    /// <summary>Named client for transit-svc (C061).</summary>
    public const string TransitClientName = "transit-svc";

    /// <summary>Named client for fare-svc.</summary>
    public const string FareClientName = "fare-svc";

    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<TransportOption>> OptionsAsync(
        GeoPoint from, GeoPoint to, CancellationToken cancellationToken)
    {
        var hasTransit = !string.IsNullOrWhiteSpace(_options.TransitBaseUrl);
        var hasFare = !string.IsNullOrWhiteSpace(_options.FareBaseUrl);

        if (!hasTransit && !hasFare)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "Neither transit-svc nor fare-svc is configured, so no transport options can be offered.");
        }

        // Both halves at once: they are independent downstreams and a passenger waiting on a
        // destination screen should wait for the slower of the two rather than their sum.
        var publicOptions = hasTransit
            ? PublicOptionsAsync(from, to, cancellationToken)
            : Task.FromResult<IReadOnlyList<TransportOption>>([]);

        var privateOptions = hasFare
            ? PrivateOptionsAsync(from, to, cancellationToken)
            : Task.FromResult<IReadOnlyList<TransportOption>>([]);

        await Task.WhenAll(publicOptions, privateOptions);

        // BR-23.2's ordering: direct public routes first, then transit options with transfers, then
        // the private tiers. A passenger comparing a Rs 30 bus with a Rs 600 taxi wants the bus at the
        // top of the list, and the tier board is a separate control on the screen anyway.
        return
        [
            .. publicOptions.Result.OrderBy(static option => option.Transfers ?? int.MaxValue),
            .. privateOptions.Result,
        ];
    }

    private async Task<IReadOnlyList<TransportOption>> PublicOptionsAsync(
        GeoPoint from, GeoPoint to, CancellationToken cancellationToken)
    {
        var url = "v1/transit/options"
                  + $"?fromLat={Coordinate(from.Latitude)}&fromLng={Coordinate(from.Longitude)}"
                  + $"&toLat={Coordinate(to.Latitude)}&toLng={Coordinate(to.Longitude)}";

        try
        {
            var response = await clients.CreateClient(TransitClientName)
                .GetFromJsonAsync<TransitOptionsResponse>(url, cancellationToken);

            return
            [
                .. (response?.Options ?? [])
                    .Where(static option => !string.IsNullOrWhiteSpace(option.ShortName)
                                            || !string.IsNullOrWhiteSpace(option.Headsign))
                    .Select(static option => new TransportOption(
                        TransportOptionKinds.Public,
                        option.Headsign ?? option.ShortName!,
                        // A GTFS route serves a bus or a train and transit-svc says which; without it
                        // the client cannot pick MAP-03's rail icon, so it is passed through as given
                        // rather than guessed at.
                        option.VehicleType,
                        option.ShortName,
                        option.FareMinor,
                        option.FareMinor is null ? null : "LKR",
                        option.Transfers ?? 0)),
            ];
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
                                            or System.Text.Json.JsonException)
        {
            // Degraded, not failed: the private tiers below are still a complete answer to "how do I
            // get there", and C061's own contract has this exact shape for a missing feed.
            logger.LogWarning(
                failure, "transit-svc did not answer; offering private tiers only (US-7.15 degraded).");

            return [];
        }
    }

    private async Task<IReadOnlyList<TransportOption>> PrivateOptionsAsync(
        GeoPoint from, GeoPoint to, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(FareClientName);

        var quotes = await Task.WhenAll(_options.PrivateTiers.Select(tier => QuoteAsync(client, tier, from, to, cancellationToken)));

        // A tier fare-svc has no tariff for is absent from the board rather than priced at zero.
        return [.. quotes.OfType<TransportOption>()];
    }

    private async Task<TransportOption?> QuoteAsync(
        HttpClient client, string vehicleType, GeoPoint from, GeoPoint to, CancellationToken cancellationToken)
    {
        var url = "v1/fare/estimate"
                  + $"?fromLat={Coordinate(from.Latitude)}&fromLng={Coordinate(from.Longitude)}"
                  + $"&toLat={Coordinate(to.Latitude)}&toLng={Coordinate(to.Longitude)}"
                  + $"&vehicleType={Uri.EscapeDataString(vehicleType)}";

        try
        {
            var quote = await client.GetFromJsonAsync<FareEstimate>(url, cancellationToken);

            return quote is null
                ? null
                // Constructed without an ETA and without a distance: AL-19 makes a pre-match tier
                // price-only, and the shape of this call is where that is enforced.
                : new TransportOption(
                    TransportOptionKinds.Private,
                    vehicleType,
                    vehicleType,
                    RouteNumber: null,
                    quote.AmountMinor,
                    quote.Currency ?? "LKR",
                    Transfers: null);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
                                            or System.Text.Json.JsonException)
        {
            logger.LogWarning(failure, "fare-svc did not price the {Tier} tier; omitting it.", vehicleType);
            return null;
        }
    }

    private static string Coordinate(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>transit-svc's <c>GET /v1/transit/options</c> shape (C061), only what is used here.</summary>
    private sealed record TransitOptionsResponse(
        [property: JsonPropertyName("options")] IReadOnlyList<TransitOptionDto>? Options);

    private sealed record TransitOptionDto(
        [property: JsonPropertyName("shortName")] string? ShortName,
        [property: JsonPropertyName("headsign")] string? Headsign,
        [property: JsonPropertyName("vehicleType")] string? VehicleType,
        [property: JsonPropertyName("transfers")] int? Transfers,
        [property: JsonPropertyName("fareMinor")] long? FareMinor);

    /// <summary>fare-svc's <c>GET /v1/fare/estimate</c> 200 (D3' <c>FareEstimate</c>).</summary>
    private sealed record FareEstimate(
        [property: JsonPropertyName("amountMinor")] long AmountMinor,
        [property: JsonPropertyName("currency")] string? Currency);
}
