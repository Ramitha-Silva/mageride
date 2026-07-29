using MageRide.Reputation;

// The pipeline itself lives in ReputationApplication so the test suite drives the same composition.
var app = ReputationApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
