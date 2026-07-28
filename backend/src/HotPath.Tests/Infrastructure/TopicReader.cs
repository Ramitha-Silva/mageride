using System.Text;
using Confluent.Kafka;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>One record read off a topic, with the headers the producer stamped.</summary>
internal sealed record TopicRecord(string Key, byte[] Value, IReadOnlyDictionary<string, string> Headers, int Partition)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Reads a Redpanda topic from the beginning, so an assertion is made against the broker rather
/// than against a service's own counter.
/// </summary>
/// <remarks>
/// <para>
/// Reading from the beginning every time is deliberate. The E-08 claim is "exactly one copy of each
/// message reached <c>telemetry.raw</c>", and the only honest way to check it is to count what is
/// actually on the topic — a bridge that double-published would still report one forward per
/// delivery. Records from earlier tests are filtered out by key: the containers are shared and every
/// test mints its own vehicle ids.
/// </para>
/// <para>
/// A fresh consumer group per read, so one test's offsets can never hide another's records.
/// </para>
/// </remarks>
internal static class TopicReader
{
    /// <summary>
    /// Reads until <paramref name="expected"/> matching records have been seen or the timeout
    /// elapses, then returns everything that matched.
    /// </summary>
    /// <remarks>
    /// It keeps reading for a short grace period <b>after</b> the expected count is reached. Without
    /// that, a duplicate-ingest bug would look like a pass: the read would stop at the first N
    /// records and never see the second copy.
    /// </remarks>
    public static async Task<IReadOnlyList<TopicRecord>> ReadAsync(
        RedpandaFixture redpanda,
        string topic,
        Func<TopicRecord, bool> matches,
        int expected,
        TimeSpan? timeout = null,
        TimeSpan? settle = null)
    {
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(matches);

        redpanda.RequireAvailable();

        var config = new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"test-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(topic);

        var found = new List<TopicRecord>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        DateTime? settleUntil = null;

        while (DateTime.UtcNow < deadline && (settleUntil is null || DateTime.UtcNow < settleUntil))
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(250));

            if (result?.Message is null)
            {
                continue;
            }

            var record = new TopicRecord(
                result.Message.Key ?? string.Empty,
                result.Message.Value ?? [],
                ReadHeaders(result.Message.Headers),
                result.Partition.Value);

            if (!matches(record))
            {
                continue;
            }

            found.Add(record);

            if (found.Count >= expected && settleUntil is null)
            {
                // Keep reading briefly: a duplicate arrives after the count is met, not before.
                settleUntil = DateTime.UtcNow + (settle ?? TimeSpan.FromSeconds(2));
            }
        }

        consumer.Close();
        return found;
    }

    private static Dictionary<string, string> ReadHeaders(Headers? headers)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);

        if (headers is null)
        {
            return read;
        }

        foreach (var header in headers)
        {
            read[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        return read;
    }
}
