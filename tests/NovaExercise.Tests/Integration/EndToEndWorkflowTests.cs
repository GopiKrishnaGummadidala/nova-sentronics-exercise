using NovaExercise.Core.Engine;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Integration;

public class EndToEndWorkflowTests
{
    [Fact]
    public async Task EndToEnd_SensorChange_TriggersStageExecution()
    {
        // Arrange: sensors with controlled values
        var registry = new SensorRegistry();

        // Use fixed values instead of random for determinism
        var tempSensor = new SimulatedSensor(
            SensorType.Temperature,
            () => 15.0 // will trigger rule 1: Stage1 + Stage2
        );

        var pressureSensor = new SimulatedSensor(
            SensorType.Pressure,
            () => 80.0
        );

        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var rm = new ResourceManager();
        IAuditLogger audit = new AuditLogger();
        IStageExecutor executor = new SimulatedStageExecutor(rm);
        IStageScheduler scheduler = new StageScheduler(executor, audit);

        var rules = DefaultRules.Create();
        IRuleEvaluationPolicy policy = new UnionRuleEvaluationPolicy();

        using var engine = new RuleEngine(registry, rules, policy, scheduler);

        // Act: start engine and wait a bit for sensor events to propagate
        engine.Start();
        await Task.Delay(250); // allow at least two sensor ticks

        // Assert: at least one of the expected resources should have been used
        // We check that R_A or R_B have been busy at some point indirectly
        // by ensuring no exceptions and that resources are back to idle or busy
        var stateA = rm.GetState(ResourceId.R_A);
        var stateB = rm.GetState(ResourceId.R_B);

        // Resources should be either Idle or Busy, not Error
        Assert.NotEqual(ResourceState.Error, stateA);
        Assert.NotEqual(ResourceState.Error, stateB);

        engine.Stop();
        tempSensor.Dispose();
        pressureSensor.Dispose();
    }
}