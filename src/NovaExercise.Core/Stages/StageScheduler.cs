using System.Collections.Concurrent;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;

namespace NovaExercise.Core.Stages;

public sealed class StageScheduler : IStageScheduler
{
    private readonly IStageExecutor _executor;
    private readonly IAuditLogger _audit;
    private readonly ConcurrentDictionary<StageId, Task> _running = new();

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
            // Reserve the slot atomically before doing any work. TryAdd either claims
            // the id exclusively or fails if another caller already owns it, closing
            // the ContainsKey-then-set race that let the same stage start twice when
            // two sensors report a reading at nearly the same time.
            if (!_running.TryAdd(id, Task.CompletedTask))
                continue;

            var def = GetStageDefinition(id);

            // Logged here - only on a genuine transition to running - rather than by
            // the caller for every rule match, so the audit trail reflects what
            // actually started rather than how many times a sensor tick re-evaluated
            // rules that still pointed at an already-running stage.
            _audit.LogStageScheduled(id, sensorValues, def.RequiredResources, DateTimeOffset.Now);

            var task = Task.Run(async () =>
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
                }
            }, ct);

            _running[id] = task;
        }

        return Task.CompletedTask;
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
