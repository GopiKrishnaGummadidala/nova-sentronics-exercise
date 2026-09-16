namespace NovaExercise.Core.Sensors;

public record SensorReading(
    SensorType Type,
    double Value,
    DateTimeOffset Timestamp
);