using Aiso.Infrastructure;
using Aiso.Infrastructure.Persistence;

// Run-to-completion Aspire node: applies EF Core migrations + seed data, then exits.
// The AppHost gates the web UI and worker on this node with WaitForCompletion().
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAisoCore();
builder.Services.AddSingleton<DatabaseSeeder>();
builder.Services.AddHostedService<DatabaseInitializer>();

var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
