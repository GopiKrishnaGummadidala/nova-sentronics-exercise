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

    void LogStageFailed(
        StageId stageId,
        Exception exception,
        DateTimeOffset timestamp
    );

    void LogRuleEvaluationFailed(
        Exception exception,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        DateTimeOffset timestamp
    );

    void LogSensorReadingFailed(
        SensorType sensorType,
        Exception exception,
        DateTimeOffset timestamp
    );
}