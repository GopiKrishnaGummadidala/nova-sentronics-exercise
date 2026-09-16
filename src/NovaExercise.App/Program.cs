using Microsoft.Extensions.DependencyInjection;

using NovaExercise.App;
using NovaExercise.Core.Engine;
using NovaExercise.Core.Sensors;

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var services = new ServiceCollection();
services.AddNovaExerciseServices();

using var provider = services.BuildServiceProvider();

// Sensors are constructed directly rather than registered in the container:
// each SimulatedSensor instance needs its own SensorType and value-generator
// lambda, so there's no single "the" ISensor implementation to register. Only
// the registry they're published through comes from DI.
var tempSensor = new SimulatedSensor(
    SensorType.Temperature,
    () => 15 + new Random().NextDouble() * 10 // 15–25 → always > 10, often > 20
);

var pressureSensor = new SimulatedSensor(
    SensorType.Pressure,
    () => 40 + new Random().NextDouble() * 40 // 40–80 → always < 100, sometimes < 50
);

var registry = provider.GetRequiredService<ISensorRegistry>();
registry.Register(tempSensor);
registry.Register(pressureSensor);

var engine = provider.GetRequiredService<IRuleEngine>();
engine.Start();

Console.WriteLine("System running. Press Ctrl+C to exit.");

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
}
catch (OperationCanceledException)
{
    // normal shutdown
}
finally
{
    engine.Stop();
    tempSensor.Dispose();
    pressureSensor.Dispose();
    Console.WriteLine("System stopped.");
}