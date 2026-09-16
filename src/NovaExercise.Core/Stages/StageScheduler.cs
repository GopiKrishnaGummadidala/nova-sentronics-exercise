using System.Collections.Concurrent;
using NovaExercise.Core.Resources;

namespace NovaExercise.Core.Stages;

public sealed class StageScheduler : IStageScheduler
{
    private readonly IStageExecutor _executor;
    private readonly ConcurrentDictionary<StageId, Task> _running = new();

    public StageScheduler(IStageExecutor executor)
    {
        _executor = executor;
    }

    public Task ScheduleStagesAsync(IReadOnlyCollection<StageId> stageIds, CancellationToken ct)
    {
        foreach (var id in stageIds)
        {
            if (_running.ContainsKey(id))
                continue;

            var def = GetStageDefinition(id);
            var task = Task.Run(async () =>
            {
                try
                {
                    await _executor.ExecuteAsync(def, ct);
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