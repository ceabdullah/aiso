namespace Aiso.Web.Services;

/// <summary>
/// Per-circuit (scoped) notifications between dashboard components: the live run strip raises
/// <see cref="AuditFinished"/> and the open tab reloads its data.
/// </summary>
public sealed class DashboardEvents
{
    public event Func<Task>? AuditFinished;

    public async Task RaiseAuditFinishedAsync()
    {
        if (AuditFinished is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            {
                await handler();
            }
        }
    }
}
