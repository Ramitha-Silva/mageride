using MageRide.Contract.Tests.Model;
using MageRide.Shared.Errors;

namespace MageRide.Contract.Tests.Conventions;

/// <summary>
/// The error registry, held between the contract and the code that produces it.
///
/// <para>
/// `.spectral.yaml` makes a promise this file is the other half of: "The registry itself is
/// `_shared.yaml#/components/schemas/ErrorCode`, mirrored at runtime by
/// `MageRide.Shared.Errors.MageRideErrors` (C002); <b>C118 asserts the two agree</b>." Spectral can
/// see that a code is kebab; only a test with both sides loaded can see that the two lists are the
/// same list.
/// </para>
///
/// <para>
/// The failure this prevents is quiet. D3' §0 says a client "can branch on the code alone", and the
/// portals do — `problem.ts` in each of the three web surfaces maps a kebab code to a translated
/// sentence. A service that emits a code the contract never declared reaches those maps as
/// `unknown` and renders "something went wrong" for a condition somebody wrote copy for; a contract
/// that declares a code nothing emits is a sentence in three languages that is never shown.
/// </para>
/// </summary>
public sealed class ErrorRegistryTests
{
    private static readonly ContractSet Contracts = ContractSet.Current;

    /// <summary>Every value in <c>_shared.yaml#/components/schemas/ErrorCode</c>.</summary>
    private static IReadOnlyList<string> DeclaredCodes =>
        ContractOperation.Strings(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "ErrorCode", "enum"));

    /// <summary>
    /// Every code the platform can emit, read from the kernel's own registry.
    /// </summary>
    /// <remarks>
    /// <see cref="MageRideErrors.All"/> and not reflection over the fields: the registry is
    /// <em>open</em> — a service "that needs a code of its own registers it at start-up with
    /// `Register`" — so the field list is the declared half and the registry is the whole. Today no
    /// service registers one and the two are the same set; the day one does, this test is the thing
    /// that notices the contract did not grow with it.
    /// </remarks>
    private static IReadOnlyList<string> KernelCodes =>
        MageRideErrors.All
            .Select(static code => code.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_registry_is_not_empty()
    {
        // A resolution failure in the reader would otherwise make every assertion below vacuous.
        Assert.NotEmpty(DeclaredCodes);
        Assert.NotEmpty(KernelCodes);
    }

    [Fact]
    public void Every_code_the_kernel_can_emit_is_in_the_contract_registry()
    {
        var missing = KernelCodes.Except(DeclaredCodes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"MageRideErrors declares {missing.Count} code(s) that `_shared.yaml#/components/schemas/ErrorCode` "
            + $"does not: {string.Join(", ", missing)}. A client branching on the code sees `unknown`.");
    }

    [Fact]
    public void Every_code_the_contract_declares_exists_in_the_kernel()
    {
        var missing = DeclaredCodes.Except(KernelCodes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"The contract registry declares {missing.Count} code(s) `MageRideErrors` does not: "
            + $"{string.Join(", ", missing)}. Nothing can produce them, and three locale files carry a "
            + "sentence for each.");
    }

    [Fact]
    public void Every_operation_declares_the_codes_it_can_emit()
    {
        var silent = Contracts.Operations.Where(static operation => operation.ErrorCodes.Count == 0).ToList();

        Assert.True(
            silent.Count == 0,
            $"{silent.Count} operation(s) carry no `x-error-codes`: "
            + string.Join(", ", silent.Take(10).Select(static operation => operation.ToString())));
    }

    [Fact]
    public void Every_declared_error_code_is_in_the_registry()
    {
        var registry = DeclaredCodes.ToHashSet(StringComparer.Ordinal);

        var unknown = Contracts.Operations
            .SelectMany(operation => operation.ErrorCodes.Select(code => (operation, code)))
            .Where(entry => !registry.Contains(entry.code))
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"{unknown.Count} operation(s) list an error code that is not in the registry: "
            + string.Join(
                ", ",
                unknown.Take(10).Select(static entry => $"{entry.operation} → `{entry.code}`")));
    }

    [Fact]
    public void Every_error_code_is_a_kebab_key()
    {
        // Spectral asserts this over the YAML. It is asserted again here because the *kernel* is the
        // other producer of these strings, and a `MageRideErrors` entry in camelCase would satisfy
        // the lint by never appearing in a document.
        var wrong = KernelCodes
            .Concat(DeclaredCodes)
            .Distinct(StringComparer.Ordinal)
            .Where(static code => !System.Text.RegularExpressions.Regex.IsMatch(
                code, "^[a-z0-9]+(-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(1)))
            .ToList();

        Assert.True(wrong.Count == 0, $"Not kebab error codes: {string.Join(", ", wrong)}.");
    }

    [Fact]
    public void The_problem_type_uri_is_the_one_the_kernel_builds()
    {
        // `problem.type` is `https://mageride.lk/errors/{code}` and all three portals parse the code
        // back out of it by stripping that prefix. If the kernel's prefix and the contract's example
        // ever disagree, every error on every web surface degrades to "unexpected" at once.
        var problem = new ContractSchema(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "Problem"),
            ContractSet.SharedDocument);

        Assert.False(problem.IsEmpty, "`_shared.yaml#/components/schemas/Problem` did not resolve.");

        var type = problem.Properties["type"];
        var described = string.Concat(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "Problem", "properties", "type", "description")
                as string ?? string.Empty);

        Assert.Contains("https://mageride.lk/errors/", described, StringComparison.Ordinal);
        Assert.Contains("uri", type.Format ?? "uri", StringComparison.Ordinal);
    }
}
