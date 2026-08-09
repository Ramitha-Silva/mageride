using System.Text.Json;
using MageRide.Contract.Tests.Model;

namespace MageRide.Contract.Tests.Runtime;

/// <summary>
/// C118's second definition-of-done item: <b>a deliberately drifted response fails the suite</b>.
///
/// <para>
/// A conformance suite's one unfalsifiable failure mode is a validator that passes everything. Every
/// other test here is written to go green; these are written to go red, against schemas taken out of
/// the real documents rather than out of a fixture, and they fail if the validator ever stops
/// noticing. Each case is a drift somebody could actually ship.
/// </para>
/// </summary>
public sealed class DriftTests
{
    private static readonly ContractSet Contracts = ContractSet.Current;

    /// <summary>The shape D3' §0 makes every error on the platform.</summary>
    private static ContractSchema Problem => new(
        Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "Problem"),
        ContractSet.SharedDocument);

    /// <summary>Integer minor units and a currency fixed at LKR.</summary>
    private static ContractSchema Money => new(
        Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "Money"),
        ContractSet.SharedDocument);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public void A_conforming_problem_passes()
    {
        // The control. Without it, every assertion below could be passing because the validator
        // rejects everything, which is the same amount of information as accepting everything.
        var conforming = Json(
            """
            {
              "type": "https://mageride.lk/errors/offer-expired",
              "title": "Offer expired",
              "status": 409,
              "detail": "The 15 s window closed.",
              "instance": "/v1/rides/01JZZ/accept",
              "traceId": "00-abc-def-01"
            }
            """);

        Assert.Empty(SchemaValidator.Validate(conforming, Problem));
    }

    [Fact]
    public void A_problem_missing_a_required_field_fails()
    {
        // `status` gone. Every portal reads it, and an error without one renders as a success.
        var drifted = Json("""{ "type": "https://mageride.lk/errors/conflict", "title": "Conflict" }""");

        var violations = SchemaValidator.Validate(drifted, Problem);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("status", StringComparison.Ordinal));
    }

    [Fact]
    public void A_problem_whose_status_became_a_string_fails()
    {
        // The drift a serializer setting produces: `409` becomes `"409"`, every client's numeric
        // branch stops matching, and nothing throws anywhere.
        var drifted = Json(
            """{ "type": "https://mageride.lk/errors/conflict", "title": "Conflict", "status": "409" }""");

        Assert.NotEmpty(SchemaValidator.Validate(drifted, Problem));
    }

    [Fact]
    public void Money_as_a_decimal_fails()
    {
        // The money bug the LKR convention exists to prevent: Rs 480.00 sent as `480.5` minor units.
        // `format: int64` is what catches it, and this is the test that proves the format is checked
        // rather than decorative.
        var drifted = Json("""{ "amountMinor": 480.5, "currency": "LKR" }""");

        var violations = SchemaValidator.Validate(drifted, Money);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("int64", StringComparison.Ordinal));
    }

    [Fact]
    public void Money_in_another_currency_fails()
    {
        // `const: LKR`. "The platform transacts in no other currency."
        var drifted = Json("""{ "amountMinor": 48000, "currency": "USD" }""");

        var violations = SchemaValidator.Validate(drifted, Money);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("LKR", StringComparison.Ordinal));
    }

    [Fact]
    public void A_conforming_amount_passes()
    {
        Assert.Empty(SchemaValidator.Validate(Json("""{ "amountMinor": 48000, "currency": "LKR" }"""), Money));
    }

    [Fact]
    public void A_cursor_page_that_omits_its_cursor_fails()
    {
        // C002 decision 9: `cursor` is always present and null on the last page, "so 'last page'
        // cannot be confused with 'field missing'". A service that omitted it would look identical
        // to one at the end of its data — to a client, and to a lazier validator than this one.
        var envelope = new ContractSchema(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "CursorPage"),
            ContractSet.SharedDocument);

        Assert.Empty(SchemaValidator.Validate(
            Json("""{ "items": [], "cursor": null, "hasMore": false }"""), envelope));

        var violations = SchemaValidator.Validate(Json("""{ "items": [], "hasMore": false }"""), envelope);
        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("cursor", StringComparison.Ordinal));
    }

    [Fact]
    public void An_enum_value_nobody_declared_fails()
    {
        // A service that grows a nineteenth ride state and ships it before the contract does.
        var state = new ContractSchema(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "RideState"),
            ContractSet.SharedDocument);

        Assert.Empty(SchemaValidator.Validate(Json("\"InProgress\""), state));
        Assert.NotEmpty(SchemaValidator.Validate(Json("\"Teleporting\""), state));
    }

    [Fact]
    public void A_route_that_moved_fails_the_route_table_check()
    {
        // The structural half of the same guarantee, and the one a schema validator cannot give.
        // A contract operation whose path the service does not serve is unmapped — asserted here by
        // constructing the drift rather than by waiting for somebody to ship it.
        var served = ServiceRoutes.Of(ServiceCatalog.All.Single(static s => s.Document == "ride"))
            .ToHashSet();

        var real = Contracts.Operations.First(static operation =>
            operation.Document == "ride"
            && operation.Method == "GET"
            && !RouteDrift.UnmappedOperations.Contains(operation.OperationId));

        Assert.Contains(new ServiceRoute(real.Method, ServiceRoutes.Normalise(real.Template)), served);

        // One segment renamed — the shape of a path that was edited in the contract and not in the
        // service, or the other way round.
        Assert.DoesNotContain(
            new ServiceRoute(real.Method, ServiceRoutes.Normalise(real.Template) + "/drifted"),
            served);

        // And the verb, which is the drift a review reads straight past.
        Assert.DoesNotContain(new ServiceRoute("PATCH", ServiceRoutes.Normalise(real.Template)), served);
    }

    [Fact]
    public void An_undeclared_error_code_would_fail_the_registry_check()
    {
        // The registry check is a set comparison, so its teeth are only visible against a value that
        // is not in the set. `MageRideErrors.TryGet` is the same lookup the kernel does when a
        // service throws, which is why an unregistered code cannot reach a client in the first place
        // — this asserts the two halves agree about that.
        Assert.True(MageRide.Shared.Errors.MageRideErrors.TryGet("offer-expired", out _));
        Assert.False(MageRide.Shared.Errors.MageRideErrors.TryGet("validation-error", out _));
    }
}
