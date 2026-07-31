using MageRide.Ocr;

// The pipeline itself lives in OcrApplication so the test suite drives the same composition.
var app = OcrApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
