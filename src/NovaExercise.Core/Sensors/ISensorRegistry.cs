namespace NovaExercise.Core.Sensors;

public interface ISensorRegistry
{
    IReadOnlyCollection<ISensor> Sensors { get; }

    /// <summary>Raised after Register() adds a new sensor, or replaces an existing
    /// one for the same SensorType (in the replacement case, SensorUnregistered for
    /// the old instance fires first).</summary>
    event Action<ISensor>? SensorRegistered;

    /// <summary>Raised after Unregister() removes a sensor, or after Register()
    /// replaces one for the same SensorType.</summary>
    event Action<ISensor>? SensorUnregistered;

    void Register(ISensor sensor);
    void Unregister(ISensor sensor);
    ISensor? GetByType(SensorType type);
}
