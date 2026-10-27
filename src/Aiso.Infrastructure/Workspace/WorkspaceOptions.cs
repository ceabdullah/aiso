namespace Aiso.Infrastructure.Workspace;

/// <summary>
/// The on-premises workspace that both this platform and <c>claude.exe</c> operate in.
/// Layout:
/// <code>
///   {RootPath}\CLAUDE.md        workspace rules read by claude.exe
///   {RootPath}\data\{runId}\    scrape snapshots + probe answers (agent input)
///   {RootPath}\reports\         exported *_AISO_remediation_plan.md (agent output)
/// </code>
/// </summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Aiso:Workspace";

    public string RootPath { get; set; } = @"C:\PROJECTS\LOCAL LLM DEMO";

    public string DataFolder { get; set; } = "data";

    public string ReportsFolder { get; set; } = "reports";

    public string DataPath => Path.Combine(RootPath, DataFolder);

    public string ReportsPath => Path.Combine(RootPath, ReportsFolder);

    /// <summary>Converts an absolute path under the workspace to a forward-slash relative path.</summary>
    public string ToRelative(string absolutePath) =>
        Path.GetRelativePath(RootPath, absolutePath).Replace('\\', '/');
}
