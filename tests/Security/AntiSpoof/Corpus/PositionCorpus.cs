using System.Text.Json;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.Security.Tests.AntiSpoof.Corpus;

/// <summary>
/// The C128 adversarial position corpus, loaded once and run through the deployed D-18/T-07 gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>It drives <see cref="PlausibilityFilter"/> itself.</b> Not a transcription of its rules, not
/// a mock, and not the pipeline around it: the class position-processor-svc resolves, with the
/// thresholds <c>infra/env/.env.app.example</c> deploys. A corpus measured against a copy of the
/// rules measures the copy, and the copy is the thing that cannot be wrong.
/// </para>
/// <para>
/// <b>The vehicle's state is carried forward exactly as the service carries it.</b> C039's ordering
/// fence — "a refused sample must not become the <c>veh:meta</c> position the next sample is
/// measured against, or a spoofer could walk a vehicle across the island one refused jump at a
/// time" — is a property of the *pipeline*, not of the filter, so a corpus that fed the filter each
/// sample against its predecessor regardless of the verdict would measure a gate the platform does
/// not have. <see cref="Run"/> advances the last-accepted position only on an accept.
/// </para>
/// </remarks>
internal static class PositionCorpus
{
    private static readonly Lazy<CorpusDocument> Document = new(Load);

    private static readonly Lazy<PositionProcessorOptions> DeployedOptions =
        new(() => DeployedConfiguration.Bind<PositionProcessorOptions>(PositionProcessorOptions.SectionName));

    /// <summary>The corpus file's contents.</summary>
    public static CorpusDocument Current => Document.Value;

    /// <summary>The thresholds the replica and the compose stacks actually run with.</summary>
    public static PositionProcessorOptions Deployed => DeployedOptions.Value;

    /// <summary>Every track, as xUnit theory data keyed by id.</summary>
    public static TheoryData<string> TrackIds()
    {
        var data = new TheoryData<string>();

        foreach (var track in Current.Tracks)
        {
            data.Add(track.Id);
        }

        return data;
    }

    public static CorpusTrack Track(string id) =>
        Current.Tracks.FirstOrDefault(track => string.Equals(track.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"No corpus track is called '{id}'.");

    /// <summary>Runs one track through the gate and reports what happened to every sample.</summary>
    public static TrackOutcome Run(CorpusTrack track, PositionProcessorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        var settings = options ?? Deployed;
        var filter = new PlausibilityFilter(Options.Create(settings));
        var samples = CorpusExpansion.Expand(track, DateTimeOffset.UtcNow);
        var verdicts = new List<SampleOutcome>(samples.Count);

        LastAcceptedPosition? last = null;

        foreach (var candidate in samples)
        {
            // `veh:meta` expiry, which is also the step gate's horizon: a vehicle silent longer
            // than VehicleMetaTtl has no last accepted position to be measured against, and its
            // next sample is accepted unchecked. Modelling the TTL is not a detail — leaving it out
            // would report the coverage-gap attacks as caught when the deployed service does not
            // catch them, which is the one direction a security measurement must never err in.
            if (last is { } held && candidate.Sample.SampleTs - held.SampleTs > settings.VehicleMetaTtl)
            {
                last = null;
            }

            var verdict = filter.Judge(candidate.Sample, last, candidate.IsReplay);

            if (verdict.IsPlausible)
            {
                // Only an accepted sample becomes the state the next one is judged against. The
                // pipeline's fence, reproduced here because it is load-bearing for the measurement.
                last = new LastAcceptedPosition(
                    new GeoPoint(candidate.Sample.Lat, candidate.Sample.Lng),
                    candidate.Sample.SampleTs,
                    candidate.Sample.Seq,
                    Cell: null,
                    Pool: null);
            }

            verdicts.Add(new SampleOutcome(candidate, verdict));
        }

        return new TrackOutcome(track, verdicts);
    }

    private static CorpusDocument Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AntiSpoof", "Corpus", "position-corpus.json");

        if (!File.Exists(path))
        {
            // The file is `Content` in the csproj; a build that stopped copying it would otherwise
            // report an empty corpus as a clean sweep.
            throw new FileNotFoundException($"The adversarial corpus was not copied to {path}.", path);
        }

        var document = JsonSerializer.Deserialize<CorpusDocument>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }) ?? throw new InvalidOperationException($"{path} deserialised to null.");

        var duplicates = document.Tracks
            .GroupBy(track => track.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        return duplicates.Count == 0
            ? document
            : throw new InvalidOperationException(
                $"The corpus has duplicate track ids, so a measurement would double-count them: "
                + string.Join(", ", duplicates));
    }
}

/// <summary>One sample and the verdict the deployed gate reached about it.</summary>
internal sealed record SampleOutcome(CorpusSample Candidate, PlausibilityVerdict Verdict)
{
    /// <summary>An honest sample the gate refused — one false positive.</summary>
    public bool IsFalsePositive => !Candidate.Hostile && !Verdict.IsPlausible;

    /// <summary>A hostile sample the gate let through — one escape.</summary>
    public bool IsEscape => Candidate.Hostile && Verdict.IsPlausible;

    /// <summary>Caught, but by a different gate than the corpus predicted.</summary>
    public bool IsMiscategorised =>
        Candidate.Hostile
        && !Verdict.IsPlausible
        && Candidate.Expect is not PlausibilityCheck.None
        && Verdict.Check != Candidate.Expect;
}

/// <summary>What the gate did to one track.</summary>
internal sealed record TrackOutcome(CorpusTrack Track, IReadOnlyList<SampleOutcome> Samples)
{
    public int HonestSamples => Samples.Count(sample => !sample.Candidate.Hostile);

    public int HostileSamples => Samples.Count(sample => sample.Candidate.Hostile);

    public int FalsePositives => Samples.Count(sample => sample.IsFalsePositive);

    /// <summary>
    /// Hostile samples the gate let through, less each leg's declared allowance.
    /// </summary>
    /// <remarks>
    /// <b>The allowance is a prediction, not a discount.</b> Some attacks are only catchable for
    /// part of their length — a recorded track replayed live is refused sample by sample right up
    /// to the point where the replay coincides with where the vehicle actually is, and from there
    /// it is indistinguishable from a parked vehicle because it <i>is</i> a parked vehicle. A leg
    /// that says <c>minRefused</c> is claiming exactly how far the gate gets, and the claim is
    /// asserted in the direction that matters: catching fewer fails.
    /// </remarks>
    public int Escapes => Samples
        .Where(sample => sample.Candidate.Hostile)
        .GroupBy(sample => sample.Candidate.Leg)
        .Sum(leg =>
        {
            var declared = Track.Legs[leg.Key].MinRefused;
            var allowed = declared is null ? 0 : Math.Max(0, leg.Count() - declared.Value);

            return Math.Max(0, leg.Count(sample => sample.IsEscape) - allowed);
        });

    /// <summary>Every hostile sample the gate let through, allowance or not — for the report.</summary>
    public int UnrefusedHostileSamples => Samples.Count(sample => sample.IsEscape);

    public int Refused => Samples.Count(sample => !sample.Verdict.IsPlausible);

    /// <summary>A line for the failure message: which sample, which gate, and why.</summary>
    public string Explain(Func<SampleOutcome, bool> predicate) => string.Join(
        Environment.NewLine,
        Samples
            .Select((sample, index) => (sample, index))
            .Where(entry => predicate(entry.sample))
            .Take(8)
            .Select(entry =>
                $"    #{entry.index} (leg {entry.sample.Candidate.Leg}): "
                + $"{entry.sample.Verdict.Check} — {entry.sample.Verdict.Detail ?? "accepted"}"));
}
