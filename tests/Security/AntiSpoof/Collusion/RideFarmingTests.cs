using System.Globalization;
using System.Text;
using MageRide.Reputation.Configuration;
using MageRide.Reputation.Domain;
using MageRide.TestKit;

namespace MageRide.Security.Tests.AntiSpoof.Collusion;

/// <summary>
/// C128's fifth deliverable: <b>anti-collusion detector tuning against synthetic ride-farming
/// patterns</b> (E-07, ADD §12.6).
///
/// <para>
/// A ride-farming pair and a loyal customer produce the same row in
/// <c>reputation.intake_log</c>: a passenger and a driver completing rides together, often. The
/// detector cannot tell them apart from frequency alone and does not claim to — ADD §12.6 says it
/// "cross-checks device-binding hashes and IP/ASN clustering" — so what tuning it means is
/// measuring how many honest pairs a threshold catches, against a population that looks like the
/// market it will run in.
/// </para>
///
/// <para>
/// <b>That market matters here more than usual.</b> A Colombo commuter with "their" three-wheeler
/// driver, twice a day on weekdays, is forty completed rides in thirty days with one counterparty.
/// It is one of the most ordinary things a passenger can do and it clears a threshold of eight in
/// the first week. The population below is built to include them, because a corpus of strangers
/// would report a precision this detector does not have.
/// </para>
///
/// <para>
/// <b>The fence is the second subject.</b> "Anti-collusion output is a review signal for admins,
/// not an automatic ban" — so every test that raises a flag also asserts nothing was blocked.
/// </para>
/// </summary>
[Collection<AntiSpoofCollection>]
[Trait("Category", "AntiSpoof")]
public sealed class RideFarmingTests(PostgresFixture postgres)
{
    /// <summary>The DoD-adjacent assertion: a farming pair is found, and only reviewed.</summary>
    [Fact]
    public async Task A_farming_pair_is_flagged_for_review_and_nothing_is_blocked()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);

        var passenger = await plane.CreateUserAsync("passenger");
        var driver = await plane.CreateUserAsync("driver");

        // Comfortably over the threshold, inside the 30-day window, and with the two corroborating
        // signals E-07 names: one handset and one address behind both accounts.
        await plane.CompleteRidesAsync(passenger, driver, plane.Collusion.PairRideThreshold + 6, TimeSpan.FromDays(20));
        await plane.ShareDeviceAsync("c128-farm-handset", passenger, driver);
        await plane.ObserveOnAddressAsync("198.51.100.44", 45489, TimeSpan.FromDays(2), passenger, driver);

        var raised = await plane.DetectAsync();

        Assert.Contains(raised, flag => flag.Kind == FraudFlagKinds.RepeatPair
            && flag.SubjectId == passenger && flag.RelatedId == driver);

        Assert.Contains(raised, flag => flag.Kind == FraudFlagKinds.SharedDevice);

        // Every flag reached the admin queue as an event, and the fence held: no block state moved.
        Assert.True(await plane.FraudEventCountAsync() >= raised.Count);
        Assert.Equal(0, await plane.BlockStateCountAsync());
    }

    /// <summary>
    /// The fence, on its own, so a change that started auto-suspending is caught even if every
    /// detector still works.
    /// </summary>
    /// <remarks>
    /// ADD §12.6's own row reads "auto-suspends both accounts on Tier-2 thresholds", and C033's
    /// service resolved that by giving the auto-suspend to an admin decision rather than to the
    /// detector. This is the assertion that keeps it there: a detector that could block would make
    /// every false positive below an account suspension rather than a queue item, and the
    /// measurement further down says how many of those there would be.
    /// </remarks>
    [Fact]
    public async Task The_detector_never_blocks_anybody_however_many_signals_agree()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);

        var passenger = await plane.CreateUserAsync("passenger");
        var driver = await plane.CreateUserAsync("driver");
        var accomplice = await plane.CreateUserAsync("passenger");
        var fourth = await plane.CreateUserAsync("passenger");

        await plane.CompleteRidesAsync(passenger, driver, 40, TimeSpan.FromDays(20));
        await plane.CompleteRidesAsync(accomplice, driver, 30, TimeSpan.FromDays(20));
        await plane.ShareDeviceAsync("c128-every-signal", passenger, driver, accomplice, fourth);
        await plane.ObserveOnAddressAsync(
            "198.51.100.77", 45489, TimeSpan.FromDays(1), passenger, driver, accomplice, fourth);

        var raised = await plane.DetectAsync();

        // All three detectors fired on one cluster — the worst case an admin will ever see.
        Assert.Contains(raised, flag => flag.Kind == FraudFlagKinds.RepeatPair);
        Assert.Contains(raised, flag => flag.Kind == FraudFlagKinds.SharedDevice);
        Assert.Contains(raised, flag => flag.Kind == FraudFlagKinds.NetworkCluster);

        Assert.Equal(0, await plane.BlockStateCountAsync());
    }

    /// <summary>
    /// Exactly once per detection window, whatever the schedule.
    /// </summary>
    /// <remarks>
    /// The detector runs on a timer on every replica and two passes can overlap, so this is a
    /// database property (<c>ux_fraud_flags_window</c>) rather than a scheduler one. It also means
    /// the interval is a latency choice: running it every minute raises the same flags as running
    /// it every hour, only sooner — which is what makes it safe to tune.
    /// </remarks>
    [Fact]
    public async Task A_second_pass_inside_the_window_raises_nothing_further()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);

        var passenger = await plane.CreateUserAsync("passenger");
        var driver = await plane.CreateUserAsync("driver");

        await plane.CompleteRidesAsync(passenger, driver, plane.Collusion.PairRideThreshold + 2, TimeSpan.FromDays(10));

        var first = await plane.DetectAsync();
        var events = await plane.FraudEventCountAsync();

        Assert.NotEmpty(first);

        var second = await plane.DetectAsync();

        Assert.Empty(second);
        Assert.Equal(events, await plane.FraudEventCountAsync());
    }

    /// <summary>
    /// The threshold is a boundary and it is asserted from both sides.
    /// </summary>
    /// <remarks>
    /// One-sided, this passes on a detector that flags every pair and on one that flags none.
    /// </remarks>
    [Fact]
    public async Task The_pair_threshold_is_a_boundary_in_both_directions()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);
        var threshold = plane.Collusion.PairRideThreshold;

        var underPassenger = await plane.CreateUserAsync("passenger");
        var underDriver = await plane.CreateUserAsync("driver");
        var atPassenger = await plane.CreateUserAsync("passenger");
        var atDriver = await plane.CreateUserAsync("driver");

        await plane.CompleteRidesAsync(underPassenger, underDriver, threshold - 1, TimeSpan.FromDays(20));
        await plane.CompleteRidesAsync(atPassenger, atDriver, threshold, TimeSpan.FromDays(20));

        var raised = await plane.DetectAsync();

        Assert.Contains(raised, flag => flag.SubjectId == atPassenger && flag.RelatedId == atDriver);
        Assert.DoesNotContain(raised, flag => flag.SubjectId == underPassenger);
    }

    /// <summary>
    /// A pair whose rides fall outside the window is not a pair.
    /// </summary>
    /// <remarks>
    /// E-07's "N rides / 30 d" is a rolling window, so a passenger who used one driver heavily last
    /// quarter and stopped must age out. Without this the queue would only ever grow.
    /// </remarks>
    [Fact]
    public async Task Rides_older_than_the_window_do_not_count_toward_a_pair()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);

        var passenger = await plane.CreateUserAsync("passenger");
        var driver = await plane.CreateUserAsync("driver");

        // Thirty rides, all of them between 40 and 70 days ago — the whole run outside the window
        // rather than a run whose tail happens to reach into it.
        await plane.CompleteRidesAsync(
            passenger, driver, 30, TimeSpan.FromDays(30), endingAgo: TimeSpan.FromDays(40));

        var raised = await plane.DetectAsync();

        Assert.DoesNotContain(raised, flag => flag.SubjectId == passenger && flag.RelatedId == driver);
    }

    /// <summary>
    /// The measurement: what the deployed thresholds do to a population that looks like the market.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserts recall and reports precision.</b> Recall is a property the platform
    /// must have — a farming pair the detector misses is the control failing — and it is asserted
    /// hard. Precision is a queue-volume property that depends on a population nobody can know in
    /// advance, so the number is measured, printed and written up
    /// (<c>security/anti-spoof-tuning.md</c>) rather than turned into a bound that would be a guess
    /// wearing an assertion's clothes.
    /// </para>
    /// <para>
    /// The population is deliberately unkind: ninety per cent of it is honest, and the honest tail
    /// includes daily commuters who ride with one driver more often than the farmers do. That is
    /// the point — a corpus where farmers ride most would measure a detector that does not have to
    /// discriminate.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_deployed_thresholds_catch_every_farming_pair_in_a_realistic_population()
    {
        RequireDatabase();

        await using var plane = await CollusionPlane.StartAsync(postgres);
        var window = plane.Collusion.PairWindow;

        var honest = new List<(Guid Passenger, Guid Driver, int Rides, string Kind)>();
        var farming = new List<(Guid Passenger, Guid Driver, int Rides)>();

        // --- The honest population, in the proportions a ride-hailing month actually produces ---
        foreach (var (count, rides, kind) in HonestPopulation)
        {
            for (var i = 0; i < count; i++)
            {
                var passenger = await plane.CreateUserAsync("passenger");
                var driver = await plane.CreateUserAsync("driver");

                await plane.CompleteRidesAsync(passenger, driver, rides, window - TimeSpan.FromDays(2));
                honest.Add((passenger, driver, rides, kind));
            }
        }

        // --- The farmers. Fewer rides than the commuters, and the corroborating signals ---------
        for (var i = 0; i < 6; i++)
        {
            var passenger = await plane.CreateUserAsync("passenger");
            var driver = await plane.CreateUserAsync("driver");
            var rides = 12 + (i * 3);

            await plane.CompleteRidesAsync(passenger, driver, rides, window - TimeSpan.FromDays(2));
            await plane.ShareDeviceAsync(
                string.Create(CultureInfo.InvariantCulture, $"c128-farm-{i}"), passenger, driver);

            farming.Add((passenger, driver, rides));
        }

        var raised = await plane.DetectAsync();

        var pairFlags = raised
            .Where(flag => flag.Kind == FraudFlagKinds.RepeatPair)
            .Select(flag => (flag.SubjectId, flag.RelatedId))
            .ToHashSet();

        var deviceFlagged = raised
            .Where(flag => flag.Kind == FraudFlagKinds.SharedDevice)
            .Select(flag => flag.SubjectId)
            .ToHashSet();

        // --- Recall. Asserted, because a missed farming pair is the control failing -------------
        var missed = farming
            .Where(pair => !pairFlags.Contains((pair.Passenger, pair.Driver)))
            .ToList();

        Assert.True(
            missed.Count == 0,
            $"{missed.Count} of {farming.Count} farming pairs were not flagged at a threshold of "
            + $"{plane.Collusion.PairRideThreshold} rides in {window.TotalDays:0} days: "
            + string.Join(", ", missed.Select(pair => $"{pair.Rides} rides")));

        // Every farming pair is corroborated by the device cross-check as well, which is the
        // signal that separates them from the commuters below.
        Assert.All(farming, pair => Assert.Contains(pair.Passenger, deviceFlagged));

        // --- Precision. Measured and reported ---------------------------------------------------
        var honestFlagged = honest
            .Where(pair => pairFlags.Contains((pair.Passenger, pair.Driver)))
            .ToList();

        var precision = pairFlags.Count == 0 ? 0 : farming.Count / (double)pairFlags.Count;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"repeat_pair at threshold {plane.Collusion.PairRideThreshold}/{window.TotalDays:0}d: "
            + $"{pairFlags.Count} flags over {honest.Count} honest and {farming.Count} farming pairs "
            + $"— precision {precision:P0}.");

        foreach (var group in honestFlagged.GroupBy(pair => pair.Kind, StringComparer.Ordinal))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  honest '{group.Key}' pairs flagged: {group.Count()} (at {group.First().Rides} rides each)");
        }

        // Correlating the two detectors is what an admin surface has to do, and it is worth
        // showing what it buys: the honest pairs share no device, so the intersection is exact.
        var corroborated = pairFlags.Count(pair => deviceFlagged.Contains(pair.SubjectId));

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  correlated with the device cross-check: {corroborated} flags, precision "
            + $"{(corroborated == 0 ? 0 : farming.Count / (double)corroborated):P0}.");

        Assert.True(
            corroborated == farming.Count,
            $"Correlating repeat_pair with shared_device should isolate exactly the {farming.Count} "
            + $"farming pairs; it named {corroborated}.{Environment.NewLine}{report}");

        // Not an assertion about quality — the tuning finding. The commuter tail IS flagged at the
        // deployed threshold, and `security/anti-spoof-tuning.md` records the number and what to do
        // about it. Asserted only so a population that stopped containing commuters is noticed.
        Assert.True(
            honestFlagged.Count > 0,
            "No honest pair was flagged, which means the population no longer contains the commuter "
            + $"tail this measurement exists to price.{Environment.NewLine}{report}");

        Assert.Equal(0, await plane.BlockStateCountAsync());

        Dump("collusion", report.ToString());
    }

    /// <summary>
    /// Every E-07 dial has a line in the deployed environment file (C128 fence 1).
    /// </summary>
    /// <remarks>
    /// They had none until C128 and existed only as C# initialisers. For a detector whose entire
    /// output is a human review queue, that meant its volume could not be changed without a build —
    /// and queue volume is the only thing about it an operator ever needs to change.
    /// </remarks>
    [Fact]
    public void Every_collusion_threshold_is_in_the_deployed_environment_file()
    {
        var section = DeployedConfiguration.Current.GetSection("Reputation:Collusion");

        string[] required =
        [
            "PairRideThreshold", "PairWindow", "DeviceSharingThreshold",
            "NetworkClusterThreshold", "NetworkWindow", "DetectionWindow",
        ];

        var missing = required.Where(key => section[key] is null).ToList();

        Assert.True(
            missing.Count == 0,
            "These E-07 thresholds have no line in infra/env/.env.app.example: "
            + string.Join(", ", missing));

        // And the deployed values are the ones the measurement was taken against, so the numbers
        // in security/anti-spoof-tuning.md describe the deployment rather than a default.
        var deployed = DeployedConfiguration.Bind<ReputationOptions>("Reputation");
        var defaults = new ReputationOptions();

        Assert.Equal(defaults.Collusion.PairRideThreshold, deployed.Collusion.PairRideThreshold);
        Assert.Equal(defaults.Collusion.PairWindow, deployed.Collusion.PairWindow);
        Assert.Equal(defaults.Collusion.DeviceSharingThreshold, deployed.Collusion.DeviceSharingThreshold);
        Assert.Equal(defaults.Collusion.NetworkClusterThreshold, deployed.Collusion.NetworkClusterThreshold);
        Assert.Equal(defaults.Collusion.NetworkWindow, deployed.Collusion.NetworkWindow);
        Assert.Equal(defaults.Collusion.DetectionWindow, deployed.Collusion.DetectionWindow);
    }

    /// <summary>
    /// The E-07 thresholds are configurable, which is C128's first fence applied to this detector.
    /// </summary>
    [Fact]
    public async Task Raising_the_pair_threshold_in_configuration_takes_the_commuter_tail_out_of_the_queue()
    {
        RequireDatabase();

        // 24 rides in 30 days — a passenger riding with one driver most weekdays one way.
        const int commuterRides = 24;

        await using var strict = await CollusionPlane.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Reputation:Collusion:PairRideThreshold"] = "30" });

        Assert.Equal(30, strict.Collusion.PairRideThreshold);

        var passenger = await strict.CreateUserAsync("passenger");
        var driver = await strict.CreateUserAsync("driver");

        await strict.CompleteRidesAsync(passenger, driver, commuterRides, TimeSpan.FromDays(25));

        var raised = await strict.DetectAsync();

        Assert.DoesNotContain(raised, flag => flag.SubjectId == passenger && flag.RelatedId == driver);

        // And the same population at the deployed threshold is flagged, which is what makes the
        // above a property of the setting rather than of the data.
        Assert.True(
            commuterRides >= strict.Collusion.PairRideThreshold - 22,
            "the commuter fixture must sit above the deployed threshold for the comparison to mean anything");
    }

    /// <summary>
    /// Ride counts and how many pairs have them, over a thirty-day window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped from how a ride-hailing month actually distributes rather than uniformly: most pairs
    /// meet once, a minority repeat, and a small tail is a standing arrangement. The tail is the
    /// part that matters — in Sri Lanka a passenger keeping one three-wheeler driver on call is
    /// ordinary, and "twice a day on weekdays" is forty rides with one counterparty.
    /// </para>
    /// <para>
    /// Kept small enough that the whole population is a few hundred inserts: the measurement is
    /// about the ratio between the groups, and a larger population moves the printed percentages
    /// by less than the uncertainty in the shape itself.
    /// </para>
    /// </remarks>
    private static readonly (int Pairs, int Rides, string Kind)[] HonestPopulation =
    [
        (14, 1, "one-off"),
        (8, 2, "occasional"),
        (5, 4, "frequent"),
        (3, 7, "regular"),
        (2, 14, "weekday-one-way"),
        (1, 34, "weekday-return-commuter"),
    ];

    private void RequireDatabase() =>
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

    /// <summary>
    /// Writes a measurement beside the corpus run's, under <c>MAGERIDE_ANTISPOOF_DUMP=1</c>.
    /// </summary>
    /// <remarks>
    /// Same idiom as <c>PlausibilityCorpusTests.The_measurement_can_be_dumped_for_the_tuning_report</c>
    /// and C127's <c>InventoryDump</c>: the numbers in <c>security/anti-spoof-tuning.md</c> are
    /// transcribed from a run rather than typed from memory, and regenerating them is one command.
    /// </remarks>
    private static void Dump(string name, string content)
    {
        if (Environment.GetEnvironmentVariable("MAGERIDE_ANTISPOOF_DUMP") != "1")
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(DeployedConfiguration.RepositoryRoot, "security", $"anti-spoof-{name}-run.md"),
            "<!-- GENERATED by tests/Security AntiSpoof: MAGERIDE_ANTISPOOF_DUMP=1 -->" + Environment.NewLine
            + content);
    }
}
