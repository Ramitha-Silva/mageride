using MageRide.HotPath.PersistenceWriter;

// The pipeline itself lives in PersistenceWriterApplication so the test suite drives the same
// composition.
var app = PersistenceWriterApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
