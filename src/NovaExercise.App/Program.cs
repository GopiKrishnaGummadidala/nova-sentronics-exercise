using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NovaExercise.App;
using NovaExercise.Core.Engine;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Sensors;

// Last-resort catch-all, registered before anything else can throw. Every
// failure path reachable through this app's own extensibility points (rules,
// stage executors, sensors) is already caught and logged via ISystemLogger -
// this exists only for something outside all of those, e.g. a bug in a
// brand-new extension point nobody has wrapped in a try/catch yet. Written
// directly to Console.Error rather than through ISystemLogger: by the time
// this fires the process is already terminating unconditionally (true for
// every unhandled exception on .NET Core and later - there is no "handle it
// and continue" option), and its state can't be trusted enough to route
// through more abstraction than necessary. This cannot prevent the crash -
// only ensure it's logged with a recognizable line instead of a raw dump.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var exception = e.ExceptionObject as Exception;
    Console.Error.WriteLine(
        $"[ERROR] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} | " +
        $"Unhandled exception - process is terminating | " +
        (exception is not null
            ? $"{exception.GetType().Name}: {exception.Message}"
            : e.ExceptionObject)
    );
};

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Read from appsettings.json, overridable by an environment variable -
// changing the tick rate is then an edit-and-restart, not a rebuild-and-
// redeploy. optional: true means a missing file just falls through to the
// 100ms default below rather than crashing the app.
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var sensorTickInterval = TimeSpan.FromMilliseconds(configuration.GetValue("SensorTickIntervalMs", 100));

var services = new ServiceCollection();
services.AddNovaExerciseServices();

using var provider = services.BuildServiceProvider();

// Sensors are constructed directly rather than registered in the container:
// each SimulatedSensor instance needs its own SensorType and value-generator
// lambda, so there's no single "the" ISensor implementation to register. Only
// the registry they're published through - and the logger, resolved once here
// and passed to both - comes from DI.
var logger = provider.GetRequiredService<ISystemLogger>();

var tempSensor = new SimulatedSensor(
    SensorType.Temperature,
    () => 15 + Random.Shared.NextDouble() * 10, // 15–25 → always > 10, often > 20
    logger,
    sensorTickInterval
);

var pressureSensor = new SimulatedSensor(
    SensorType.Pressure,
    () => 40 + Random.Shared.NextDouble() * 40, // 40–80 → always < 100, sometimes < 50
    logger,
    sensorTickInterval
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