using MageRide.Payout;

// The composition root is PayoutApplication so the test suite drives the same pipeline.
var app = PayoutApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync();

/// <summary>Exposed so the test host can reference this assembly's entry point.</summary>
public partial class Program;
