using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Logging;

public interface IAuditLogger
{
    void LogStageScheduled(
        StageId stageId,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        IReadOnlyCollection<ResourceId> requiredResources,
        DateTimeOffset timestamp
    );
}