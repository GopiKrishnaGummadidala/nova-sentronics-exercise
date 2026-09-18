using NovaExercise.Core.Engine;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
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
    // suite runs alongside StageMapResourceContentionTests, which adds real
    // load of its own from genuine resource contention (its stage map is
    // deliberately built so only one of its stages can hold resources at a time).
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
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
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
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
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
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
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
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
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
        var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
        engine.Start();
        engine.Dispose();

        tempSensor.Emit(25.0);
        pressureSensor.Emit(30.0);

        Assert.False(await scheduler.WaitForCallAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(scheduler.Calls);
    }

    [Fact]
    public async Task SensorRegisteredAfterConstruction_IsSubscribedTo_AndItsReadingsAreProcessed()
    {
        // The exercise explicitly requires "new sensors may be introduced" as the
        // process evolves. Registering a sensor is meaningless if a RuleEngine
        // that's already running never learns about it - this is the core
        // capability that was missing before RuleEngine reacted to
        // SensorRegistered/SensorUnregistered instead of only reading the
        // registry once, in its constructor.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        registry.Register(tempSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
        engine.Start();

        // Registered only now - after the engine is already constructed and running.
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(pressureSensor);
        pressureSensor.Emit(30.0);

        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));
        var call = Assert.Single(scheduler.Calls);
        Assert.Equal(30.0, call.SensorValues[SensorType.Pressure]);
    }

    [Fact]
    public async Task SensorUnregisteredAfterConstruction_StopsAffectingEvaluation()
    {
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
        engine.Start();

        registry.Unregister(pressureSensor);
        pressureSensor.Emit(999.0); // should no longer reach the engine at all

        Assert.False(await scheduler.WaitForCallAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(scheduler.Calls);
    }

    [Fact]
    public async Task SensorReplaced_OldInstanceNoLongerAffectsEvaluation_ReplacementDoes()
    {
        // "Existing sensors may be replaced" - registering a second sensor for a
        // SensorType that's already occupied (e.g. swapping in a real hardware
        // sensor for the simulated one) must move the subscription across, not
        // leave the engine listening to the old instance, the new one, or both.
        var registry = new SensorRegistry();
        var originalTemp = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(originalTemp);
        registry.Register(pressureSensor);

        var scheduler = new RecordingStageScheduler();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, new AuditLogger());
        engine.Start();

        var replacementTemp = new FakeSensor(SensorType.Temperature);
        registry.Register(replacementTemp);

        // The old instance is no longer wired up at all - this must be ignored.
        originalTemp.Emit(999.0);
        Assert.False(await scheduler.WaitForCallAsync(TimeSpan.FromMilliseconds(200)));

        // The replacement's readings are what the engine reacts to now.
        replacementTemp.Emit(25.0);
        Assert.True(await scheduler.WaitForCallAsync(CallTimeout));
        Assert.Equal(25.0, scheduler.Calls[^1].SensorValues[SensorType.Temperature]);
    }

    [Fact]
    public async Task EvaluateAndScheduleAsync_RuleThrows_LogsTheFailure_AndDoesNotScheduleAnything()
    {
        // EvaluateAndScheduleAsync's returned Task is discarded by the
        // fire-and-forget call in OnReadingChanged - before this was fixed, a
        // throwing rule predicate faulted that discarded Task silently, with no
        // crash and no record anywhere. Rules are an explicit extensibility
        // point ("new rules may be introduced"), so this is a real, reachable
        // failure mode, not just a defensive catch.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var throwingRule = new StageRule(
            _ => throw new InvalidOperationException("Simulated rule bug"),
            new[] { StageId.Stage1 });

        var scheduler = new RecordingStageScheduler();
        var audit = new SpyAuditLogger();
        using var engine = new RuleEngine(registry, new[] { throwingRule }, Policy, scheduler, audit);
        engine.Start();

        tempSensor.Emit(25.0);

        Assert.True(await audit.WaitForFailureAsync(CallTimeout));
        Assert.Equal(25.0, audit.LastFailureSensorValues![SensorType.Temperature]);
        Assert.IsType<InvalidOperationException>(audit.LastFailureException);

        // The exception happened before the scheduler was ever reached.
        Assert.False(await scheduler.WaitForCallAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(scheduler.Calls);
    }

    [Fact]
    public async Task EvaluateAndScheduleAsync_SchedulerThrows_LogsTheFailure()
    {
        // EvaluateAndScheduleAsync's try/catch originally only wrapped
        // _policy.Evaluate(...), not the ScheduleStagesAsync call after it - so an
        // exception from the scheduler itself (e.g. StageScheduler.GetStageDefinition
        // throwing for a StageId a newly-added rule points at but has no case for)
        // would escape the fire-and-forget Task uncaught and unlogged. This verifies
        // the scheduler call is now covered by the same catch.
        var registry = new SensorRegistry();
        var tempSensor = new FakeSensor(SensorType.Temperature);
        var pressureSensor = new FakeSensor(SensorType.Pressure);
        registry.Register(tempSensor);
        registry.Register(pressureSensor);

        var scheduler = new ThrowingStageScheduler();
        var audit = new SpyAuditLogger();
        using var engine = new RuleEngine(registry, Rules, Policy, scheduler, audit);
        engine.Start();

        tempSensor.Emit(25.0);

        Assert.True(await audit.WaitForFailureAsync(CallTimeout));
        Assert.IsType<InvalidOperationException>(audit.LastFailureException);
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

    private sealed class SpyAuditLogger : IAuditLogger
    {
        private readonly SemaphoreSlim _signal = new(0);

        public Exception? LastFailureException { get; private set; }
        public IReadOnlyDictionary<SensorType, double>? LastFailureSensorValues { get; private set; }

        public void LogStageScheduled(
            StageId stageId,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            IReadOnlyCollection<ResourceId> requiredResources,
            DateTimeOffset timestamp)
        {
        }

        public void LogStageFailed(StageId stageId, Exception exception, DateTimeOffset timestamp)
        {
        }

        public void LogRuleEvaluationFailed(
            Exception exception,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            DateTimeOffset timestamp)
        {
            LastFailureException = exception;
            LastFailureSensorValues = sensorValues;
            _signal.Release();
        }

        public void LogSensorReadingFailed(SensorType sensorType, Exception exception, DateTimeOffset timestamp)
        {
        }

        public Task<bool> WaitForFailureAsync(TimeSpan timeout) => _signal.WaitAsync(timeout);
    }

    /// <summary>Always throws, simulating a bug reachable only once a new rule points
    /// at a StageId the scheduler has no case for - verifies EvaluateAndScheduleAsync's
    /// try/catch covers the ScheduleStagesAsync call itself, not just rule evaluation.</summary>
    private sealed class ThrowingStageScheduler : IStageScheduler
    {
        public Task ScheduleStagesAsync(
            IReadOnlyCollection<StageId> stageIds,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            CancellationToken ct)
        {
            throw new InvalidOperationException("Simulated scheduler bug");
        }
    }
}
