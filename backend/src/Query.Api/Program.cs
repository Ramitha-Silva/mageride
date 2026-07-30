using MageRide.Query;

// The pipeline itself lives in QueryApplication so the test suite drives the same composition.
var app = QueryApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
