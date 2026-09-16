namespace NovaExercise.Core.Sensors;

public interface ISensorRegistry
{
    IReadOnlyCollection<ISensor> Sensors { get; }
    void Register(ISensor sensor);
    void Unregister(ISensor sensor);
    ISensor? GetByType(SensorType type);
}