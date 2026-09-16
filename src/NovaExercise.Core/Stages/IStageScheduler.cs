using NovaExercise.Core.Sensors;

namespace NovaExercise.Core.Stages;

public interface IStageScheduler
{
    Task ScheduleStagesAsync(
        IReadOnlyCollection<StageId> stageIds,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        CancellationToken ct
    );
}
