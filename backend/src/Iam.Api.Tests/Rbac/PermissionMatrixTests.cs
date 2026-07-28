using System.Text.RegularExpressions;
using MageRide.Iam.Rbac;
using MageRide.Shared.Auth;

namespace MageRide.Iam.Tests.Rbac;

/// <summary>
/// DoD: "the RBAC matrix test covers every (role, privileged endpoint) pair in URD §2.3 with an
/// explicit allow/deny" — the (area, role) half.
/// </summary>
/// <remarks>
/// <para>
/// The expectation is not written here. It is <b>parsed out of
/// <c>specs/user-requirements-document.md</c> §2.3</b> and compared against the compiled matrix
/// cell for cell, so this asserts what "matching URD §2.3 exactly" actually claims: a transcription
/// slip fails, and so does a change to the spec that nobody carried into the code. Hand-copying
/// the table into the test would only prove that two copies of my typing agree.
/// </para>
/// <para>
/// The endpoint half — every (role, privileged endpoint) pair — is
/// <c>Integration/RbacEndpointTests</c>.
/// </para>
/// </remarks>
public sealed partial class PermissionMatrixTests
{
    /// <summary>URD §2.3's column order: DRV · PAX · FLT · ADM · S.ADM · VER · CSR · FIN · AUD.</summary>
    private static readonly string[] ColumnHeaders =
        ["DRV", "PAX", "FLT", "ADM", "S.ADM", "VER", "CSR", "FIN", "AUD"];

    private static readonly Lazy<IReadOnlyList<UrdRow>> Spec = new(ParseUrd);

    [Fact]
    public void The_spec_table_is_readable()
    {
        // A silent zero here would make every other assertion in this class vacuously true.
        Assert.Equal(FeatureAreas.All.Count, Spec.Value.Count);
        Assert.All(Spec.Value, row => Assert.Equal(ColumnHeaders.Length, row.Cells.Count));
    }

    [Fact]
    public void Every_feature_area_is_transcribed_in_spec_order_with_the_spec_wording()
    {
        var expected = Spec.Value.Select(static row => row.Label).ToArray();
        var actual = FeatureAreas.All.Select(static area => area.Label).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Feature_area_keys_are_unique_kebab_identifiers()
    {
        Assert.Equal(FeatureAreas.All.Count, FeatureAreas.All.Select(static a => a.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FeatureAreas.All, area => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", area.Key));
    }

    [Fact]
    public void The_column_order_matches_the_spec_header()
    {
        Assert.Equal(
            ColumnHeaders.Length,
            PermissionMatrix.Columns.Count);

        // Every canonical role appears exactly once — the matrix is total over the nine roles.
        Assert.Equal(
            MageRideRoles.All.OrderBy(static r => r, StringComparer.Ordinal),
            PermissionMatrix.Columns.OrderBy(static r => r, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_cell_matches_the_spec_symbol()
    {
        var mismatches = new List<string>();

        for (var row = 0; row < Spec.Value.Count; row++)
        {
            var area = FeatureAreas.All[row];

            for (var column = 0; column < ColumnHeaders.Length; column++)
            {
                var expected = Spec.Value[row].Cells[column];
                var actual = PermissionMatrix.Cell(area, PermissionMatrix.Columns[column]).Symbol;

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    mismatches.Add($"{area.Key} × {ColumnHeaders[column]}: spec '{expected}', code '{actual}'");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Every_role_and_area_pair_has_an_explicit_cell()
    {
        // 21 × 9 = 189. Deny-by-default means "explicitly ➖", not "absent".
        var pairs = 0;

        foreach (var area in FeatureAreas.All)
        {
            var cells = PermissionMatrix.Row(area);

            foreach (var role in MageRideRoles.All)
            {
                Assert.True(cells.ContainsKey(role), $"{area.Key} has no cell for {role}.");
                pairs++;
            }
        }

        Assert.Equal(FeatureAreas.All.Count * MageRideRoles.All.Count, pairs);
    }

    /// <summary>
    /// An area nobody transcribed denies everybody — the C027 fence, at the matrix level.
    /// </summary>
    [Fact]
    public void An_unmapped_feature_area_denies_every_role()
    {
        var invented = new FeatureArea("wallet-teleportation", "Not a URD §2.3 row");

        foreach (var role in MageRideRoles.All)
        {
            var cell = PermissionMatrix.Cell(invented, role);

            Assert.Equal(PermissionGrant.None, cell.Grants);
            Assert.False(cell.Satisfies(PermissionGrant.Read));
            Assert.False(cell.Satisfies(PermissionGrant.Write));
        }
    }

    [Theory]
    // The legend, one case per glyph (URD §2.3 "Legend").
    [InlineData("✅", PermissionGrant.Read | PermissionGrant.Write, null)]
    [InlineData("⚙", PermissionGrant.Read | PermissionGrant.Configure, null)]
    [InlineData("👁", PermissionGrant.Read, null)]
    [InlineData("➖", PermissionGrant.None, null)]
    [InlineData("◐ own", PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.OwnScope, "own")]
    [InlineData("◐ own org", PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.OwnScope, "own org")]
    [InlineData("raise", PermissionGrant.Raise, null)]
    [InlineData("report", PermissionGrant.Raise, null)]
    // The three cells that write a verb into the qualifier and mean it.
    [InlineData("✅ read", PermissionGrant.Read, "read")]
    [InlineData("◐ raise/recommend", PermissionGrant.Read | PermissionGrant.Raise | PermissionGrant.OwnScope, "raise/recommend")]
    [InlineData("◐ subset", PermissionGrant.Read | PermissionGrant.Configure | PermissionGrant.OwnScope, "subset")]
    [InlineData("⚙ rates", PermissionGrant.Read | PermissionGrant.Configure, "rates")]
    [InlineData("✅ approve/execute", PermissionGrant.Read | PermissionGrant.Write, "approve/execute")]
    public void The_legend_parses_the_way_the_spec_reads(string symbol, PermissionGrant grants, string? qualifier)
    {
        var cell = PermissionCell.Parse(symbol);

        Assert.Equal(grants, cell.Grants);
        Assert.Equal(qualifier, cell.Qualifier);
        Assert.Equal(symbol, cell.Symbol);
    }

    [Fact]
    public void An_unknown_glyph_is_refused_rather_than_read_as_a_grant()
    {
        // A symbol nobody recognises must not silently become "some access".
        Assert.Throws<ArgumentException>(() => PermissionCell.Parse("★ everything"));
    }

    /// <summary>The Auditor writes nowhere — URD §2.4: "no write access anywhere".</summary>
    [Fact]
    public void The_auditor_never_holds_write_or_configure()
    {
        foreach (var area in FeatureAreas.All)
        {
            var cell = PermissionMatrix.Cell(area, MageRideRoles.Auditor);

            Assert.False(
                cell.Grants.HasFlag(PermissionGrant.Write) || cell.Grants.HasFlag(PermissionGrant.Configure),
                $"The auditor holds {FeatureAreas.Describe(cell.Grants)} on {area.Key}; URD §2.4 gives them read only.");
        }
    }

    /// <summary>
    /// URD §2.4: "Admin — … **No** RBAC/role management." The matrix cell agrees, and this is the
    /// one that surprises people.
    /// </summary>
    [Fact]
    public void An_admin_holds_nothing_on_role_management()
    {
        Assert.Equal(
            PermissionGrant.None,
            PermissionMatrix.Cell(FeatureAreas.RoleManagement, MageRideRoles.Admin).Grants);

        Assert.Equal(
            PermissionGrant.Read | PermissionGrant.Write,
            PermissionMatrix.Cell(FeatureAreas.RoleManagement, MageRideRoles.SuperAdmin).Grants);

        Assert.Equal(
            PermissionGrant.Read,
            PermissionMatrix.Cell(FeatureAreas.RoleManagement, MageRideRoles.Auditor).Grants);
    }

    // -------------------------------------------------------------------------------------------
    // Reading §2.3 out of the spec
    // -------------------------------------------------------------------------------------------

    private sealed record UrdRow(string Label, IReadOnlyList<string> Cells);

    private static IReadOnlyList<UrdRow> ParseUrd()
    {
        var lines = File.ReadAllLines(SpecPath());
        var rows = new List<UrdRow>();
        var inTable = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (!inTable)
            {
                // The header row of the §2.3 table, and only it: nine role columns after "Feature Area".
                if (trimmed.StartsWith("| Feature Area |", StringComparison.Ordinal))
                {
                    inTable = true;
                }

                continue;
            }

            if (!trimmed.StartsWith('|'))
            {
                break;
            }

            var cells = trimmed.Trim('|').Split('|').Select(static cell => cell.Trim()).ToArray();

            // The markdown alignment row.
            if (cells.All(static cell => cell.Length == 0 || cell.All(static c => c is ':' or '-')))
            {
                continue;
            }

            rows.Add(new UrdRow(Bold().Replace(cells[0], "$1"), cells[1..]));
        }

        return rows;
    }

    /// <summary>Walks up from the test output to the repository's <c>specs/</c>, like C008's ContractCatalog.</summary>
    private static string SpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "specs", "user-requirements-document.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"specs/user-requirements-document.md was not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"\*\*(.*?)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();
}
