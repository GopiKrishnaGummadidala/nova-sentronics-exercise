using System.Collections.Concurrent;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;

namespace NovaExercise.Core.Stages;

public sealed class StageScheduler : IStageScheduler
{
    private readonly IStageExecutor _executor;
    private readonly IAuditLogger _audit;
    private readonly ConcurrentDictionary<StageId, TaskCompletionSource> _running = new();

    public StageScheduler(IStageExecutor executor, IAuditLogger audit)
    {
        _executor = executor;
        _audit = audit;
    }

    public Task ScheduleStagesAsync(
        IReadOnlyCollection<StageId> stageIds,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        CancellationToken ct)
    {
        foreach (var id in stageIds)
        {
            // The dictionary holds this placeholder for the *entire* lifetime of the
            // execution - inserted here, removed only in ExecuteStageAsync's finally -
            // rather than being overwritten afterward with Task.Run's own returned
            // handle. That earlier design left a real window open: Task.Run only
            // hands the caller a Task handle *after* queuing the work, and a
            // fast-completing executor's worker thread can reach its own
            // finally { TryRemove } before this thread reaches the line that writes
            // that handle in - silently reinserting a now-stale entry that nothing
            // will ever remove again. Verified directly: an executor returning
            // Task.CompletedTask got permanently stuck after 2 of 200,000 attempts.
            // Reserving the final value up front closes the window entirely - TryAdd
            // and TryRemove are the only two places that ever touch this entry.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_running.TryAdd(id, completion))
                continue;

            var def = GetStageDefinition(id);

            // Logged here - only on a genuine transition to running - rather than by
            // the caller for every rule match, so the audit trail reflects what
            // actually started rather than how many times a sensor tick re-evaluated
            // rules that still pointed at an already-running stage.
            _audit.LogStageScheduled(id, sensorValues, def.RequiredResources, DateTimeOffset.Now);

            _ = Task.Run(() => ExecuteStageAsync(id, def, ct, completion));
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteStageAsync(
        StageId id,
        StageDefinition def,
        CancellationToken ct,
        TaskCompletionSource completion)
    {
        try
        {
            await _executor.ExecuteAsync(def, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - not a failure worth logging.
        }
        catch (Exception ex)
        {
            _audit.LogStageFailed(id, ex, DateTimeOffset.Now);
        }
        finally
        {
            _running.TryRemove(id, out _);
            completion.TrySetResult();
        }
    }

    private static StageDefinition GetStageDefinition(StageId id)
    {
        return id switch
        {
            StageId.Stage1 => new StageDefinition(StageId.Stage1,
                new[] { ResourceId.R_A, ResourceId.R_B }),
            StageId.Stage2 => new StageDefinition(StageId.Stage2,
                new[] { ResourceId.R_C, ResourceId.R_B }),
            StageId.Stage3 => new StageDefinition(StageId.Stage3,
                new[] { ResourceId.R_A, ResourceId.R_C }),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, null)
        };
    }
}
