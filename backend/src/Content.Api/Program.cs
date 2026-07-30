using MageRide.Content;

// The pipeline itself lives in ContentApplication so the test suite drives the same composition.
var app = ContentApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
