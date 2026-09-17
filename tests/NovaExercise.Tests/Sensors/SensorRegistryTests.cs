using NovaExercise.Core.Sensors;

namespace NovaExercise.Tests.Sensors;

public class SensorRegistryTests
{
    [Fact]
    public void Register_NewSensorType_RaisesSensorRegistered()
    {
        var registry = new SensorRegistry();
        ISensor? registered = null;
        registry.SensorRegistered += s => registered = s;

        var sensor = new FakeSensor(SensorType.Temperature);
        registry.Register(sensor);

        Assert.Same(sensor, registered);
    }

    [Fact]
    public void Register_ReplacingExistingSensorType_RaisesUnregisteredForOldThenRegisteredForNew()
    {
        // "Existing sensors may be replaced" - Register() for an already-occupied
        // SensorType must tell subscribers to drop the old instance before picking
        // up the new one, in that order, so a swap can never leave both wired up
        // or neither.
        var registry = new SensorRegistry();
        var original = new FakeSensor(SensorType.Temperature);
        registry.Register(original);

        var events = new List<string>();
        registry.SensorUnregistered += s => events.Add($"Unregistered:{ReferenceEquals(s, original)}");
        registry.SensorRegistered += s => events.Add($"Registered:{ReferenceEquals(s, original)}");

        var replacement = new FakeSensor(SensorType.Temperature);
        registry.Register(replacement);

        Assert.Equal(new[] { "Unregistered:True", "Registered:False" }, events);
        Assert.Same(replacement, registry.GetByType(SensorType.Temperature));
    }

    [Fact]
    public void Register_SameInstanceAgain_DoesNotRaiseUnregisteredForItself()
    {
        var registry = new SensorRegistry();
        var sensor = new FakeSensor(SensorType.Temperature);
        registry.Register(sensor);

        var unregisteredCalled = false;
        registry.SensorUnregistered += _ => unregisteredCalled = true;

        registry.Register(sensor); // same instance, re-registered

        Assert.False(unregisteredCalled);
    }

    [Fact]
    public void Unregister_RemovesSensorAndRaisesSensorUnregistered()
    {
        var registry = new SensorRegistry();
        var sensor = new FakeSensor(SensorType.Temperature);
        registry.Register(sensor);

        ISensor? unregistered = null;
        registry.SensorUnregistered += s => unregistered = s;

        registry.Unregister(sensor);

        Assert.Same(sensor, unregistered);
        Assert.Null(registry.GetByType(SensorType.Temperature));
    }

    [Fact]
    public void Unregister_InstanceAlreadyReplaced_DoesNotRemoveTheReplacementOrRaiseAnEvent()
    {
        // A caller holding a reference to a sensor that's since been replaced via
        // Register() must not be able to tear down its replacement by calling
        // Unregister() with the stale reference.
        var registry = new SensorRegistry();
        var original = new FakeSensor(SensorType.Temperature);
        registry.Register(original);

        var replacement = new FakeSensor(SensorType.Temperature);
        registry.Register(replacement);

        var unregisteredCalled = false;
        registry.SensorUnregistered += _ => unregisteredCalled = true;

        registry.Unregister(original); // stale reference - already replaced

        Assert.False(unregisteredCalled);
        Assert.Same(replacement, registry.GetByType(SensorType.Temperature));
    }

    private sealed class FakeSensor : ISensor
    {
        public SensorType Type { get; }
        public SensorReading? CurrentReading { get; private set; }
        public event Action<SensorReading>? ReadingChanged;

        public FakeSensor(SensorType type) => Type = type;

        public void Emit(double value)
        {
            var reading = new SensorReading(Type, value, DateTimeOffset.Now);
            CurrentReading = reading;
            ReadingChanged?.Invoke(reading);
        }
    }
}
