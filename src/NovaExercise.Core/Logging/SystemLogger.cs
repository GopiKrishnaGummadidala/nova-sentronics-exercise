using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Logging;

public sealed class SystemLogger : ISystemLogger
{
    public void LogStageScheduled(
        StageId stageId,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        IReadOnlyCollection<ResourceId> requiredResources,
        DateTimeOffset timestamp)
    {
        var t = sensorValues.GetValueOrDefault(SensorType.Temperature, 0);
        var p = sensorValues.GetValueOrDefault(SensorType.Pressure, 0);

        var resources = string.Join(", ", requiredResources.OrderBy(r => r));

        Console.WriteLine(
            $"[AUDIT] {timestamp:yyyy-MM-dd HH:mm:ss.fff} | " +
            $"Stage={stageId} | " +
            $"Sensors=Temperature:{t:F2}, Pressure:{p:F2} | " +
            $"Resources={resources}"
        );
    }

    public void LogStageFailed(StageId stageId, Exception exception, DateTimeOffset timestamp)
    {
        Console.WriteLine(
            $"[ERROR] {timestamp:yyyy-MM-dd HH:mm:ss.fff} | " +
            $"Stage={stageId} | FAILED | " +
            $"{exception.GetType().Name}: {exception.Message}"
        );
    }

    public void LogRuleEvaluationFailed(
        Exception exception,
        IReadOnlyDictionary<SensorType, double> sensorValues,
        DateTimeOffset timestamp)
    {
        var t = sensorValues.GetValueOrDefault(SensorType.Temperature, 0);
        var p = sensorValues.GetValueOrDefault(SensorType.Pressure, 0);

        Console.WriteLine(
            $"[ERROR] {timestamp:yyyy-MM-dd HH:mm:ss.fff} | " +
            $"RuleEvaluation FAILED | " +
            $"Sensors=Temperature:{t:F2}, Pressure:{p:F2} | " +
            $"{exception.GetType().Name}: {exception.Message}"
        );
    }

    public void LogSensorReadingFailed(SensorType sensorType, Exception exception, DateTimeOffset timestamp)
    {
        Console.WriteLine(
            $"[ERROR] {timestamp:yyyy-MM-dd HH:mm:ss.fff} | " +
            $"Sensor={sensorType} | FAILED | " +
            $"{exception.GetType().Name}: {exception.Message}"
        );
    }
}