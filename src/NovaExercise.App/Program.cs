using NovaExercise.Core.Engine;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Logging;

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var registry = new SensorRegistry();

var tempSensor = new SimulatedSensor(
    SensorType.Temperature,
    () => 15 + new Random().NextDouble() * 10 // 15–25 → always > 10, often > 20
);

var pressureSensor = new SimulatedSensor(
    SensorType.Pressure,
    () => 40 + new Random().NextDouble() * 40 // 40–80 → always < 100, sometimes < 50
);

registry.Register(tempSensor);
registry.Register(pressureSensor);

IResourceManager resourceManager = new ResourceManager();

IAuditLogger audit = new AuditLogger();
IStageExecutor executor = new SimulatedStageExecutor(resourceManager);
IStageScheduler scheduler = new StageScheduler(executor, audit);

var rules = DefaultRules.Create();
IRuleEvaluationPolicy policy = new UnionRuleEvaluationPolicy();

using var engine = new RuleEngine(registry, rules, policy, scheduler);
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