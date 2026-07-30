using System.ComponentModel.DataAnnotations;

namespace MageRide.Query.Configuration;

/// <summary>
/// query-svc's settings. Every knob is argued at its declaration; the ones no spec pins say so.
/// </summary>
public sealed class QueryOptions
{
    public const string SectionName = "Query";

    // -------------------------------------------------------------------------------------------
    // The live plane (US-7.1, US-7.7, US-7.16, US-7.17)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How old a vehicle's last fix may be and still be drawn (US-7.17).
    /// </summary>
    /// <remarks>
    /// <b>Must equal <c>Fanout:FreshnessWindow</c>.</b> No spec pins either, and both were set to
    /// match <c>Dispatch:PresenceTtl</c> so the map, the snapshot and the candidate pool age a
    /// vehicle out together. If they drift, a vehicle disappears from the socket and comes back on
    /// the next poll, or the reverse — which reads to a passenger as a flickering marker and to an
    /// operator as nothing at all.
    /// </remarks>
    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>D3' <c>GET /v1/nearby</c>: <c>radius</c> defaults to 3000 m.</summary>
    [Range(100, 50_000)]
    public int DefaultRadiusM { get; set; } = 3_000;

    /// <summary>The contract's ceiling on <c>radius</c>.</summary>
    [Range(100, 100_000)]
    public int MaxRadiusM { get; set; } = 20_000;

    /// <summary>
    /// How many vehicles one snapshot may carry.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> A bound is needed because <c>geo:live</c> has no expiry — nothing
    /// removes a member, so a <c>GEOSEARCH</c> over a 20 km radius after a year of operation returns
    /// every vehicle that has ever driven through it, and each candidate costs a <c>veh:meta</c>
    /// read. 500 is comfortably above what fits on a phone screen at 3 km and far below what would
    /// make the read expensive. Truncation is <b>counted and logged</b>, never silent.
    /// </remarks>
    [Range(1, 10_000)]
    public int MaxVehicles { get; set; } = 500;

    /// <summary>
    /// Enforce the D-22/D-23/US-7.16/US-7.17 visibility rules. On.
    /// </summary>
    /// <remarks>
    /// Off means every vehicle in radius is returned to everybody, including engaged taxis and
    /// unshared Mode B vehicles. Announced at start-up for the reason fanout-svc and
    /// position-processor-svc announce theirs: an open filter looks exactly like a working one.
    /// </remarks>
    public bool VisibilityEnabled { get; set; } = true;

    /// <summary>Check <c>share:{userId}</c> before returning a Mode B vehicle (D-23). On.</summary>
    public bool EntitlementEnabled { get; set; } = true;

    /// <summary>
    /// Include the caller's own accepted vehicle even while it is engaged (US-7.16's second half). On.
    /// </summary>
    /// <remarks>
    /// This is the only path on which <c>driverName</c> and <c>registrationNumber</c> are ever
    /// populated (US-7.12), so switching it off does not leak anything — it hides the passenger's
    /// own car from their own map.
    /// </remarks>
    public bool OwnRideEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // ETA (US-7.11)
    // -------------------------------------------------------------------------------------------

    /// <summary>Populate <c>etaSeconds</c>. On.</summary>
    public bool EtaEnabled { get; set; } = true;

    /// <summary>
    /// How much longer the road is than the straight line.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it, and this is an interim.</b> ADD §7.6 puts routing (OSRM/Valhalla) in
    /// Phase 3, so there is no road network to measure against in Phase 1 and a routed ETA cannot be
    /// computed. 1.3 is the ordinary detour ratio for a dense urban grid. The number is a setting and
    /// not a constant precisely because it is a guess that should be retuned against observed
    /// arrivals — and it disappears entirely when the router lands.
    /// </remarks>
    [Range(1.0, 3.0)]
    public double EtaDetourFactor { get; set; } = 1.3;

    /// <summary>
    /// Below this the vehicle's reported speed is not used for the estimate.
    /// </summary>
    /// <remarks>
    /// A taxi stopped at a light has a reported speed near zero, and dividing by it gives an ETA of
    /// hours or of infinity. Under the floor the per-type figure below is used instead.
    /// </remarks>
    [Range(0.0, 20.0)]
    public double EtaMinSpeedMps { get; set; } = 2.0;

    /// <summary>
    /// Assumed average speed per canonical vehicle type, km/h, for when the reported speed is
    /// unusable.
    /// </summary>
    /// <remarks>
    /// <b>No spec gives these.</b> They are ordinary Sri Lankan urban averages including stops, not
    /// the ADD §12.6 anti-spoof ceilings — those are the speeds above which a fix is a lie, which is
    /// three to five times what a vehicle averages across a city. A type not listed falls back to
    /// <see cref="DefaultEtaSpeedKph"/>.
    /// </remarks>
    public Dictionary<string, int> EtaSpeedKph { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["motorbike"] = 30,
        ["three_wheeler"] = 25,
        ["flex"] = 28,
        ["sedan"] = 28,
        ["mini_van"] = 26,
        ["van"] = 26,
        ["truck"] = 22,
        ["mini_truck"] = 24,
        ["bus"] = 20,
        ["train"] = 45,
    };

    /// <summary>Fallback for a vehicle type with no entry above.</summary>
    [Range(1, 200)]
    public int DefaultEtaSpeedKph { get; set; } = 25;

    /// <summary>
    /// Longest ETA worth showing.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> An estimate of two hours for a bus 40 km away is arithmetically fine and
    /// useless on a map; above the cap the field is omitted rather than filled with a number nobody
    /// should act on.
    /// </remarks>
    public TimeSpan MaxEta { get; set; } = TimeSpan.FromMinutes(90);

    // -------------------------------------------------------------------------------------------
    // Geocoding — self-hosted Nominatim (D-14, D-15, D6' §7.6)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Base URL of the self-hosted Nominatim (ADD §6: its own 8 GB VPS, never co-located).
    /// </summary>
    /// <remarks>
    /// Unset means <c>/v1/geo/reverse</c> answers <c>503 dependency-unavailable</c> and
    /// <c>/v1/geo/search</c> falls back to the caller's own saved and recent places — which is a
    /// real, useful answer and is said loudly at start-up so it is not mistaken for a working
    /// geocoder. <b>There is no Google Places fallback and there must never be one</b> (D3' map hard
    /// rule).
    /// </remarks>
    public string? NominatimBaseUrl { get; set; }

    /// <summary>
    /// Sent as <c>User-Agent</c>. Nominatim's usage policy requires an identifying one even
    /// self-hosted, and it is what an operator greps its access log for.
    /// </summary>
    public string NominatimUserAgent { get; set; } = "MageRide-query-svc/1.0 (+https://mageride.lk)";

    /// <summary>
    /// ISO 3166-1 alpha-2 codes Nominatim is restricted to, comma-separated. Empty lifts the limit.
    /// </summary>
    /// <remarks>
    /// The extract is Sri Lanka only (D6' §7.6), so an unrestricted query cannot return anything
    /// else anyway; sending the filter makes that explicit rather than accidental, and keeps it true
    /// if the extract is ever widened.
    /// </remarks>
    public string CountryCodes { get; set; } = "lk";

    /// <summary>
    /// How long a geocode result is cached in Redis.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> The underlying data changes on D-15's <i>weekly</i> osm-pipeline
    /// refresh, so a day is an order of magnitude inside the interval at which an answer could
    /// change, and the same handful of queries ("Colombo Fort", a pin on a junction) dominate the
    /// traffic.
    /// </remarks>
    public TimeSpan GeocodeCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Reverse-geocode results are cached against a coordinate rounded to this many decimals.</summary>
    /// <remarks>
    /// <b>No spec.</b> Five decimals is ~1 m and would never hit; four is ~11 m, which is finer than
    /// consumer GNSS error and coarse enough that a pin dragged a few metres reuses the answer.
    /// </remarks>
    [Range(1, 6)]
    public int ReverseCacheDecimals { get; set; } = 4;

    /// <summary>Saved addresses offered alongside geocoded places (BR-23.1).</summary>
    [Range(0, 50)]
    public int SavedPlaceLimit { get; set; } = 5;

    /// <summary>Recent destinations offered alongside geocoded places (BR-23.1).</summary>
    [Range(0, 50)]
    public int RecentPlaceLimit { get; set; } = 5;

    // -------------------------------------------------------------------------------------------
    // Destination options (US-7.15) — delegated, never computed here
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// transit-svc's base URL (C061). Unset means <c>/v1/transport-options</c> returns no public
    /// options and says so at start-up.
    /// </summary>
    public string? TransitBaseUrl { get; set; }

    /// <summary>
    /// fare-svc's base URL. Unset means <c>/v1/transport-options</c> returns no private tiers.
    /// </summary>
    public string? FareBaseUrl { get; set; }

    /// <summary>
    /// The Mode C passenger tiers offered as private options, in display order.
    /// </summary>
    /// <remarks>
    /// AL-09's canonical list minus <c>truck</c>/<c>mini_truck</c>, which are delivery tiers, and
    /// minus <c>bus</c>/<c>train</c>, which are Mode A. A tier with no <c>fares.tariffs</c> row is
    /// dropped by fare-svc's own answer, not filtered here.
    /// </remarks>
    public IList<string> PrivateTiers { get; } =
        ["motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van"];

    // -------------------------------------------------------------------------------------------
    // Read scaling (ADD §9.3) and the internal surface
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// DSN of a Postgres read replica. Unset means every read goes to the primary.
    /// </summary>
    /// <remarks>
    /// ADD §9.3: "query-svc reads from replicas with read-after-write consistency only where
    /// required." <see cref="Persistence.IQueryConnectionFactory"/> is where "where required" is
    /// decided, and it is decided by the shape of the read rather than by a caller's opinion.
    /// </remarks>
    public string? ReplicaConnectionString { get; set; }

    /// <summary>
    /// The interim shared secret the gRPC surface demands, as <c>x-mageride-internal-key</c>.
    /// </summary>
    /// <remarks>
    /// Unset leaves the gRPC endpoint <b>unmapped</b>, exactly as trip-state-svc leaves
    /// <c>/v1/internal/sessions/**</c> unmapped: a deployment that forgets the secret gets an
    /// unimplemented service rather than an open one. Replaced by the mTLS peer identity when the
    /// mesh lands.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    /// <summary>Map the <c>query.v1.Query</c> gRPC service. On, but still gated on the secret above.</summary>
    public bool GrpcEnabled { get; set; } = true;

    /// <summary>
    /// The HTTP/1.1 port the REST routes are served on, when <c>ASPNETCORE_URLS</c> says nothing.
    /// </summary>
    /// <remarks>
    /// 5000 is what <c>gateway-routes.json</c> points the <c>query-svc</c> cluster at. 0 binds an
    /// ephemeral port, which is what the test harness uses.
    /// </remarks>
    [Range(0, 65_535)]
    public int HttpListenPort { get; set; } = 5_000;

    /// <summary>
    /// The dedicated HTTP/2 port <c>query.v1.Query</c> is served on.
    /// </summary>
    /// <remarks>
    /// <b>Not a preference.</b> Cleartext HTTP has no ALPN, so Kestrel cannot negotiate HTTP/1.1 and
    /// HTTP/2 on one socket and a gRPC client's preface to the REST port is answered
    /// <c>GOAWAY HTTP_1_1_REQUIRED</c>. D7' §4.2 gives reputation-svc a <c>Grpc__ListenPort</c> for the
    /// same reason and has <b>no row for query-svc</b> — micro-change-set in the C042 handoff. 5006
    /// rather than reputation's 5005 because both services run in the combined <c>app-services</c>
    /// container in the dev compose.
    /// </remarks>
    [Range(0, 65_535)]
    public int GrpcListenPort { get; set; } = 5_006;
}
