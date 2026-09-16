namespace NovaExercise.Core.Sensors;

public interface ISensor
{
    SensorType Type { get; }
    event Action<SensorReading> ReadingChanged;
    SensorReading? CurrentReading { get; }
}