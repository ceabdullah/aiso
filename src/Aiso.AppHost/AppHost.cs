// AISO distributed application topology.
//
//   sql (SQL Server container, persistent volume)  ──or──  external Oracle connection string
//     └─ aisodb
//          ├─ migrations  (run-to-completion: EF Core MigrateAsync + seed)
//          ├─ web         (Blazor Server dashboard)      waits for migrations
//          └─ worker      (pipeline + retention; launches claude.exe)  waits for migrations
//
// The worker runs as a host process (not a container) on purpose: it must be able to start the
// locally installed claude.exe and read/write the on-prem workspace folder.

var builder = DistributedApplication.CreateBuilder(args);

var provider = builder.Configuration["Aiso:Database:Provider"] ?? "SqlServer";
var useOracle = string.Equals(provider, "Oracle", StringComparison.OrdinalIgnoreCase);
// The default workspace is the on-prem Windows path; on macOS/Linux development machines a Windows path
// can't exist, so fall back to ~/AISO-Workspace unless a non-Windows path is configured.
var configuredRoot = builder.Configuration["Aiso:Workspace:RootPath"];
var workspaceRoot = OperatingSystem.IsWindows()
    ? configuredRoot ?? @"C:\PROJECTS\LOCAL LLM DEMO"
    : configuredRoot is { Length: > 0 } root && !root.Contains('\\', StringComparison.Ordinal)
        ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AISO-Workspace");
Directory.CreateDirectory(workspaceRoot);

// claude.exe analysis needs the corporate LiteLLM gateway, which a developer laptop can't reach.
// Off by default outside Windows so no request is sent with a placeholder key; set Aiso:ClaudeCli:Enabled to override.
var claudeCliEnabled = builder.Configuration["Aiso:ClaudeCli:Enabled"] ?? (OperatingSystem.IsWindows() ? "true" : "false");

// Secrets: supply via `dotnet user-secrets set Parameters:llm-api-key <key>` (dev) or the
// deployment environment (Parameters__llm-api-key). Never commit them.
var llmApiKey = builder.AddParameter("llm-api-key", secret: true);
var llmBaseUrl = builder.AddParameter("llm-base-url", "https://teb.com.tr", publishValueAsDefault: true);
var llmModel = builder.AddParameter("llm-model", "claude-3-5-sonnet-20241022", publishValueAsDefault: true);

IResourceBuilder<IResourceWithConnectionString> database;
IResourceBuilder<SqlServerServerResource>? sqlServer = null;
if (useOracle)
{
    // Remote Oracle: ConnectionStrings:aisodb = "User Id=...;Password=...;Data Source=host:1521/SERVICE"
    database = builder.AddConnectionString("aisodb");
}
else
{
    var sqlPassword = builder.AddParameter("sql-password", secret: true);
    sqlServer = builder.AddSqlServer("sql", password: sqlPassword)
        .WithDataVolume("aiso-sql-data")
        .WithLifetime(ContainerLifetime.Persistent);
    database = sqlServer.AddDatabase("aisodb");
}

var migrations = builder.AddProject<Projects.Aiso_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Aiso__Database__Provider", provider);

// Public AI answer engines (ChatGPT, Gemini, Perplexity, Claude). Each key is optional: set
// Parameters:<engine>-api-key in user secrets or the environment to switch that engine on.
string[] publicEngines = ["chatgpt", "gemini", "perplexity", "claude"];
var engineKeys = publicEngines
    .Where(engine => !string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{engine}-api-key"]))
    .Select(engine => (Engine: engine, Key: builder.AddParameter($"{engine}-api-key", secret: true)))
    .ToList();

var worker = builder.AddProject<Projects.Aiso_Worker>("worker")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithEnvironment("Aiso__Database__Provider", provider)
    .WithEnvironment("Aiso__Workspace__RootPath", workspaceRoot)
    .WithEnvironment("Aiso__Llm__BaseUrl", llmBaseUrl)
    .WithEnvironment("Aiso__Llm__Model", llmModel)
    .WithEnvironment("Aiso__Llm__ApiKey", llmApiKey)
    .WithEnvironment("Aiso__ClaudeCli__Enabled", claudeCliEnabled);

foreach (var (engine, key) in engineKeys)
{
    worker.WithEnvironment($"Aiso__Engines__{engine}__ApiKey", key);
}

var web = builder.AddProject<Projects.Aiso_Web>("web")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithEnvironment("Aiso__Database__Provider", provider)
    .WithEnvironment("Aiso__Workspace__RootPath", workspaceRoot)
    .WithExternalHttpEndpoints();

if (sqlServer is not null)
{
    migrations.WaitFor(database);
    worker.WaitFor(database);
    web.WaitFor(database);
}

builder.Build().Run();
