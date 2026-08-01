using MageRide.PublicBff;

// The pipeline itself lives in PublicBffApplication so the test suite drives the same composition —
// including the start-up guard that makes "no Bearer auth, ever" a refusal rather than a convention.
var app = PublicBffApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
