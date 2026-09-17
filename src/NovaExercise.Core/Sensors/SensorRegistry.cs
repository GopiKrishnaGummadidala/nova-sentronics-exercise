using System.Collections.Concurrent;

namespace NovaExercise.Core.Sensors;

public sealed class SensorRegistry : ISensorRegistry
{
    private readonly ConcurrentDictionary<SensorType, ISensor> _sensors = new();

    // A plain lock, not relying on ConcurrentDictionary's own atomicity: Register
    // needs to atomically capture whichever sensor it's replacing (if any) together
    // with storing the new one, so it can raise exactly the right pair of events -
    // ConcurrentDictionary's indexer alone doesn't hand back the value it replaced.
    // Registration changes are assumed to be infrequent, operator-initiated events,
    // not high-frequency traffic like sensor readings, so a lock here costs nothing
    // in practice.
    private readonly object _lock = new();

    public event Action<ISensor>? SensorRegistered;
    public event Action<ISensor>? SensorUnregistered;

    public IReadOnlyCollection<ISensor> Sensors => _sensors.Values.ToList().AsReadOnly();

    public void Register(ISensor sensor)
    {
        ISensor? previous;
        lock (_lock)
        {
            _sensors.TryGetValue(sensor.Type, out previous);
            _sensors[sensor.Type] = sensor;
        }

        // Replacing an existing sensor of the same type: tell subscribers to drop
        // the old one before picking up the new one, so a swap can never leave both
        // wired up (double-counting readings) or neither (silently going dark).
        if (previous is not null && !ReferenceEquals(previous, sensor))
            SensorUnregistered?.Invoke(previous);

        SensorRegistered?.Invoke(sensor);
    }

    public void Unregister(ISensor sensor)
    {
        lock (_lock)
        {
            // Only remove if this exact instance is still the one registered -
            // otherwise a caller holding a reference to a sensor that was already
            // replaced via Register() could incorrectly remove its replacement.
            if (!_sensors.TryGetValue(sensor.Type, out var current) || !ReferenceEquals(current, sensor))
                return;

            _sensors.TryRemove(sensor.Type, out _);
        }

        SensorUnregistered?.Invoke(sensor);
    }

    public ISensor? GetByType(SensorType type) =>
        _sensors.TryGetValue(type, out var s) ? s : null;
}
