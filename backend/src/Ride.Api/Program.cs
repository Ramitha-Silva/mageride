using MageRide.Ride;

// The pipeline itself lives in RideApplication so the test suite drives the same composition.
var app = RideApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
