using MageRide.Transit;

// The pipeline itself lives in TransitApplication so the test suite drives the same composition.
var app = TransitApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
