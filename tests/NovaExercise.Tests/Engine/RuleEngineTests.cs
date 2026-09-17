using NovaExercise.Core.Engine;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Engine;

public class RuleEngineTests
{
    private static readonly IReadOnlyList<StageRule> Rules = DefaultRules.Create();
    private static readonly IRuleEvaluationPolicy Policy = new UnionRuleEvaluationPolicy();
    // A ceiling, not a typical duration - this returns as soon as its signal
    // arrives. Widened from 2s after observing an occasional miss when the full
    // suite runs alongside StageMapResourceContentionTests, which adds
    // substantial real thread-pool load of its own (heavy Acquire polling).
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadingFromOneSensor_EvaluatesWithOtherSensorDefaultingToZero()
    {
        // The Pressure sensor has never reported anything when Temperature ticks.
        // RuleEngine only knows about readings it has actually received, so this
        // exercises the same "missing reading defaults to 0" behavior covered in
        // RuleBoundaryTests, but through the real event path instead of calling the
        // policy directly - confirming RuleEngine doesn't add its own guard against it.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure); // never emits
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler);
        engine.Start();

        tempSensor.Emit(25.0); // Pressure defaults to 0, which satisfies every "< N" condition

        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));
        var call = Assert.Single(scheduler.Calls);
        Assert.Equal(
            new HashSet<StageId> { StageId.Stage1, StageId.Stage2, StageId.Stage3 },
            new HashSet<StageId>(call.StageIds));
        Assert.Equal(25.0, call.SensorValues[SensorType.Temperature]);
        Assert.False(call.SensorValues.ContainsKey(SensorType.Pressure)); // never reported - not defaulted in the snapshot itself
    }

    [Fact]
    public async Task SecondReading_RemembersThePriorValueOfTheSensorThatDidNotChange()
    {
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler);
        engine.Start();

        tempSensor.Emit(25.0);
        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));

        pressureSensor.Emit(40.0); // only Pressure changes this time
        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));

        var lastCall = scheduler.Calls[^1];
        Assert.Equal(25.0, lastCall.SensorValues[SensorType.Temperature]); // remembered from the earlier reading
        Assert.Equal(40.0, lastCall.SensorValues[SensorType.Pressure]);
    }

    [Fact]
    public async Task Constructor_CapturesASensorsExistingReading_AsABaselineBeforeAnyNewEvent()
    {
        // If a sensor already had a reading before RuleEngine subscribed (e.g. it
        // started ticking before the engine finished construction), the constructor
        // captures that as a baseline instead of only learning about it on the
        // sensor's *next* tick - see the subscribe-then-snapshot ordering in
        // RuleEngine's constructor.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        tempSensor.Emit(25.0); // reading exists before the engine is even constructed
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler);
        engine.Start();

        pressureSensor.Emit(30.0); // the only *new* event after construction

        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));
        var call = Assert.Single(scheduler.Calls);
        // Both values are present even though only Pressure ticked after construction.
        Assert.Equal(25.0, call.SensorValues[SensorType.Temperature]);
        Assert.Equal(30.0, call.SensorValues[SensorType.Pressure]);
    }

    [Fact]
    public async Task StoppedEngine_StillForwardsAReadingItReceives_WithAnAlreadyCancelledToken()
    {
        // Stop() cancels RuleEngine's internal token but does not unsubscribe from
        // sensors - only Dispose() does. So a reading delivered after Stop() still
        // gets evaluated and forwarded to the scheduler, just carrying a token that's
        // already cancelled. Documented here as the engine's actual behavior rather
        // than assumed to be unreachable.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler);
        engine.Start();
        engine.Stop();

        tempSensor.Emit(25.0);
        pressureSensor.Emit(30.0);

        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));
        Assert.True(scheduler.Calls[^1].CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposedEngine_NoLongerReactsToSensorReadings()
    {
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        var engine = new RuleEngine(registry, Rules, Policy, scheduler);
        engine.Start();
        engine.Dispose();

        tempSensor.Emit(25.0);
        pressureSensor.Emit(30.0);

        Assert.False(await scheduler.WaitForCallAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(scheduler.Calls);
    }

    /// <summary>
    /// Fires ReadingChanged on command instead of a 100ms background timer, so tests
    /// can assert on a specific reading at a specific instant instead of racing a
    /// real SimulatedSensor via Task.Delay.
    /// </summary>
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

    /// <summary>
    /// Records every ScheduleStagesAsync call instead of actually running stages, and
    /// signals a semaphore per call so tests can wait for RuleEngine's fire-and-forget
    /// evaluation to reach the scheduler instead of assuming it completes synchronously.
    /// WaitForCallAsync uses WaitAsync, not the blocking Wait - a synchronous wait would
    /// tie up a real thread-pool thread inside an async test, and under a full parallel
    /// test run (many test classes at once, only as many OS threads as the pool can
    /// spare) that starves the pool and can make even multi-second timeouts miss.
    /// </summary>
    private sealed class RecordingStageScheduler : IStageScheduler
    {
        public sealed record Call(
            IReadOnlyCollection<StageId> StageIds,
            IReadOnlyDictionary<SensorType, double> SensorValues,
            CancellationToken CancellationToken);

        private readonly List<Call> _calls = new();
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<Call> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public Task ScheduleStagesAsync(
            IReadOnlyCollection<StageId> stageIds,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            CancellationToken ct)
        {
            lock (_calls)
            {
                _calls.Add(new Call(stageIds, sensorValues, ct));
            }
            _signal.Release();
            return Task.CompletedTask;
        }

        public Task<bool> WaitForCallAsync(TimeSpan timeout) => _signal.WaitAsync(timeout);
    }
}
