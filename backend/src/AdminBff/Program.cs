using MageRide.AdminBff;

// The pipeline itself lives in AdminBffApplication so the test suite drives the same composition —
// including the start-up guards that make D-35 and AL-02 refusals rather than review comments.
var app = AdminBffApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
