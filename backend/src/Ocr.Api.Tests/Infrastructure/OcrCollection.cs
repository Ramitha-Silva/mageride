using MageRide.TestKit;

namespace MageRide.Ocr.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so this suite
/// declares its own over the TestKit's fixture rather than hand-rolling a container.
/// </para>
/// <para>
/// <b>Postgres is load-bearing beyond "a place to put rows."</b> Migration 1310's
/// <c>ck_extractions_gemini_is_redacted</c> is the database's own statement of D-36 — a row saying
/// the external model ran on an unredacted image is one it will not store — and 1301's
/// <c>ck_extractions_status</c>, the <c>docs.uploads</c> foreign key and
/// <c>auto_delete_at</c>'s <c>now() + interval</c> are all claims about the server rather than
/// about this process.
/// </para>
/// <para>
/// <b>No Redis and no Redpanda fixture</b>, because the service opens neither — see
/// <c>OcrApplication</c> for why each is off. Asking for one here would start a container this
/// suite has nothing to assert against.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class OcrCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-ocr";
}
