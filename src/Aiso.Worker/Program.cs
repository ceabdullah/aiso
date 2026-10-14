using Aiso.Infrastructure;
using Aiso.Worker.Maintenance;
using Aiso.Worker.Pipeline;

// Headless processing node: audit pipeline (scrape → probe → claude.exe → reports) + retention.
// Runs under Aspire in development and as a Windows Service on-premises.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAisoPipeline();
builder.Services.AddWindowsService(o => o.ServiceName = "AISO Worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditPipelineService>();
builder.Services.AddHostedService<AuditPipelineWorker>();
builder.Services.AddHostedService<WeeklyLogCleanUpWorker>();

var host = builder.Build();
await host.RunAsync();
