using Aiso.Domain;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Settings;

namespace Aiso.Worker.Pipeline;

/// <summary>
/// Writes <see cref="AuditRunStatus"/> to settings as the run progresses, for the dashboard's live strip.
/// Engines answer in parallel, so updates are serialised. Progress is best effort: a failed write never
/// fails the audit.
/// </summary>
public sealed class RunProgressReporter(ISystemSettingsService settings, ILogger logger, int targetsTotal, int answersTotal) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private string _stage = "Starting";
    private string? _target;
    private string? _lastEngine;
    private int _targetsDone;
    private int _answersDone;

    public Task StageAsync(string stage, string? target, CancellationToken ct) =>
        UpdateAsync(() => (_stage, _target) = (stage, target), ct);

    public Task AnswerAsync(string engine, CancellationToken ct) =>
        UpdateAsync(() => (_answersDone, _lastEngine) = (_answersDone + 1, engine), ct);

    public Task TargetDoneAsync(CancellationToken ct) =>
        UpdateAsync(() => _targetsDone++, ct);

    public async Task ClearAsync()
    {
        try
        {
            await settings.SetAsync(SettingKeys.AuditRunStatus, string.Empty, "worker", CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not clear the audit run status.");
        }
    }

    private async Task UpdateAsync(Action change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            change();
            var status = new AuditRunStatus(_startedAtUtc, DateTime.UtcNow, _stage, _target,
                _targetsDone, targetsTotal, _answersDone, answersTotal, _lastEngine);
            await settings.SetAsync(SettingKeys.AuditRunStatus, status.ToJson(), "worker", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write the audit run status.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
