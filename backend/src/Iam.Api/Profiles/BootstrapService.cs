using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;

namespace MageRide.Iam.Profiles;

/// <summary>AL-14's eager-fetch login payload — US-1.15's six items, in one object.</summary>
public sealed record LoginBootstrap(
    UserProfile Profile,
    IReadOnlyList<string> Roles,
    FleetScope? Fleet,
    IReadOnlyList<SavedAddress> SavedAddresses,
    IReadOnlyList<EmergencyContact> EmergencyContacts,
    IReadOnlyList<string> PaymentMethods,
    ActiveTrip? ActiveTrip,
    DriverShift? Driver,
    IReadOnlyList<OperatingCity> Cities,
    IReadOnlyDictionary<string, bool> FeatureFlags,
    EffectivePermissionSet Permissions);

/// <summary><c>GET /v1/me/bootstrap</c>.</summary>
public interface IBootstrapService
{
    Task<LoginBootstrap> BuildAsync(Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBootstrapService"/>
/// <remarks>
/// <para>
/// <b>One connection, one round trip, nothing unbounded.</b> NFR-51 makes the login payload
/// bounded and US-1.14 makes it the thing a driver's replacement handset restores a live trip
/// from. Everything here is a single-row read or the three-row launch-city list; trip history,
/// earnings breakdowns and receipts are lazy-fetched per screen (US-1.16) and must never be
/// added.
/// </para>
/// <para>
/// The reads are sequential on one connection rather than concurrent on several. A login is not
/// latency-critical the way a dispatch offer is, and six pooled connections per sign-in would
/// cost more under load than the handful of milliseconds it saves on an idle box.
/// </para>
/// </remarks>
public sealed class BootstrapService(
    INpgsqlConnectionFactory connections,
    IProfileRepository profiles,
    IUserRepository users,
    ISavedAddressRepository addresses,
    IEmergencyContactRepository contacts,
    IBootstrapRepository bootstrap,
    IPermissionEvaluator policies,
    TimeProvider clock) : IBootstrapService
{
    public async Task<LoginBootstrap> BuildAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var profile = await profiles.FindAsync(connection, null, userId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Unauthorized, "The account behind this token no longer exists.");

        var principal = await users.PrincipalAsync(connection, null, userId, cancellationToken);
        var savedAddresses = await addresses.ListAsync(connection, null, userId, cancellationToken);
        var emergencyContacts = await contacts.ListAsync(connection, null, userId, cancellationToken);

        // The Mode C ride first: it is the one with a passenger waiting, and R-01 keeps the two
        // planes apart, so a person cannot legitimately be on both. If they somehow are, the ride
        // is the one whose state machine has a counterparty in it.
        var activeTrip = await bootstrap.FindActiveRideAsync(connection, userId, cancellationToken);

        var isDriver = principal.Roles.Contains(MageRideRoles.Driver, StringComparer.Ordinal);

        DriverShift? driver = null;
        if (isDriver)
        {
            var session = await bootstrap.FindActiveSessionAsync(connection, userId, cancellationToken);
            activeTrip ??= session;

            var businessDate = BusinessCalendar.Today(clock);
            var earnings = await bootstrap.FindEarningsAsync(connection, userId, businessDate, cancellationToken);

            driver = new DriverShift(
                // Online means "there is a live trip on either plane" — a Mode A/B session, or a
                // Mode C ride in flight. dispatch.driver_presence is C034's and is a heartbeat,
                // not a fact this payload can wait on.
                IsOnline: session is not null || activeTrip is { Role: "driver" },
                ActiveSessionId: session?.TripId,
                ActiveVehicleId: session?.VehicleId ?? activeTrip?.VehicleId,
                BusinessDate: businessDate,
                TodayTrips: earnings?.Trips ?? 0,
                TodayGross: new Money(earnings?.GrossMinor ?? 0, earnings?.Currency ?? Money.Lkr),
                TodayDailyFee: new Money(earnings?.DailyFeeMinor ?? 0, earnings?.Currency ?? Money.Lkr));
        }

        var cities = await bootstrap.ActiveCitiesAsync(connection, cancellationToken);

        return new LoginBootstrap(
            profile,
            principal.Roles,
            principal.Fleet,
            savedAddresses,
            emergencyContacts,
            [.. ProfileService.PaymentMethods],
            activeTrip,
            driver,
            cities,
            // No feature-flag store exists — ADD §1.12 gives a Super Admin "feature flags" and no
            // spec models a table for them (C027 handoff). An empty object is the honest shape:
            // the field is in the contract, so a client can rely on it being there, and it starts
            // answering the day the store lands without a client change.
            new Dictionary<string, bool>(StringComparer.Ordinal),
            policies.Evaluate(userId, principal.Roles, principal.Fleet));
    }
}
