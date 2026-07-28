using MageRide.HotPath.PositionProcessor;

// The pipeline itself lives in PositionProcessorApplication so the test suite drives the same
// composition.
var app = PositionProcessorApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
