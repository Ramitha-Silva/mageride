using MageRide.TripState;

// The pipeline itself lives in TripStateApplication so the test suite drives the same composition.
var app = TripStateApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
