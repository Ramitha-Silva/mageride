using MageRide.Fare;

// The pipeline itself lives in FareApplication so the test suite drives the same composition.
var app = FareApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
