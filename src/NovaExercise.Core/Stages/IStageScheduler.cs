using NovaExercise.Core.Resources;

namespace NovaExercise.Core.Stages;

public interface IStageScheduler
{
    Task ScheduleStagesAsync(
        IReadOnlyCollection<StageId> stageIds,
        CancellationToken ct
    );
}