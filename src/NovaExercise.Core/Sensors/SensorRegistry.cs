using System.Collections.Concurrent;

namespace NovaExercise.Core.Sensors;

public sealed class SensorRegistry : ISensorRegistry
{
    private readonly ConcurrentDictionary<SensorType, ISensor> _sensors = new();

    public IReadOnlyCollection<ISensor> Sensors => _sensors.Values.ToList().AsReadOnly();

    public void Register(ISensor sensor) =>
        _sensors[sensor.Type] = sensor;

    public void Unregister(ISensor sensor) =>
        _sensors.TryRemove(sensor.Type, out _);

    public ISensor? GetByType(SensorType type) =>
        _sensors.TryGetValue(type, out var s) ? s : null;
}