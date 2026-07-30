using MageRide.FleetHealth.Ingest;
using MageRide.Provisioning.Trackers;

namespace MageRide.FleetHealth.Tests.Domain;

/// <summary>
/// The four <c>provisioning.events</c> names this service spells for itself, against the ones
/// provisioning-svc actually publishes.
/// </summary>
/// <remarks>
/// <para>
/// <c>FleetHealth</c> does not reference <c>Provisioning.Api</c> — a stream consumer must not compile
/// against its producer's code — so the names are written down on both sides of the fence. The
/// divergence this guards is silent: a renamed event makes <see cref="ProvisioningEventConsumer"/>
/// commit every message unread, and every fleet dashboard then shows a roster of trackers that never
/// leave <c>Offline</c> and never become <c>Decommissioned</c>. Nothing throws and nothing logs.
/// </para>
/// <para>
/// The same seam C040 asserts for <c>session.ended</c> and C043 for the <c>prov:tracker</c> signal.
/// </para>
/// </remarks>
public sealed class BindingEventNamesTests
{
    [Fact]
    public void The_four_event_names_match_provisioning_svc()
    {
        Assert.Equal(TrackerEventTypes.TrackerBound, ProvisioningEventConsumer.TrackerBound);
        Assert.Equal(TrackerEventTypes.TrackerUnbound, ProvisioningEventConsumer.TrackerUnbound);
        Assert.Equal(TrackerEventTypes.TrackerRevoked, ProvisioningEventConsumer.TrackerRevoked);
        Assert.Equal(TrackerEventTypes.TrackerQuarantined, ProvisioningEventConsumer.TrackerQuarantined);
    }

    [Fact]
    public void The_two_events_this_service_ignores_are_still_the_two_it_names()
    {
        // Recorded rather than merely omitted: a rotation deliberately leaves the outgoing credential
        // valid (C030), and a source switch says which of a phone and a tracker publishes — neither is
        // a health fact, and mapping either would make a routine 90-day renewal look like an outage.
        Assert.Equal("tracker.credential_rotated", TrackerEventTypes.CredentialRotated);
        Assert.Equal("tracker.source_switched", TrackerEventTypes.SourceSwitched);
    }
}
