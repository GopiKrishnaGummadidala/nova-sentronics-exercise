using System.Diagnostics;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Stages;

public class StageSchedulerTests
{
    // A ceiling, not a typical duration - these waits return as soon as their
    // signal arrives. Widened from 2s after observing an occasional miss when
    // the full suite runs alongside StageMapResourceContentionTests, which adds
    // substantial real thread-pool load of its own (heavy Acquire polling).
    private static readonly TimeSpan WaitCeiling = TimeSpan.FromSeconds(5);


    [Fact]
    public async Task ScheduleStagesAsync_DoesNotThrowForValidStages()
    {
        var rm = new ResourceManager();
        IStageExecutor executor = new SimulatedStageExecutor(rm);
        IStageScheduler scheduler = new StageScheduler(executor, new AuditLogger());

        var stages = new[] { StageId.Stage1, StageId.Stage2 };
        var sensorValues = new Dictionary<SensorType, double>();

        var cts = new CancellationTokenSource();

        // Should not throw
        await scheduler.ScheduleStagesAsync(stages, sensorValues, cts.Token);

        // Give some time for background tasks to start
        await Task.Delay(200);

        cts.Cancel();
    }

    [Fact]
    public async Task ScheduleStagesAsync_ConcurrentCallsForSameStage_NeverRunsMoreThanOneAtOnce()
    {
        // Regression test for a check-then-act race: two independent sensor threads
        // can call ScheduleStagesAsync for the same stage at nearly the same time.
        // The scheduler must guarantee at most one in-flight execution per stage id.
        var executor = new TrackingStageExecutor();
        IStageScheduler scheduler = new StageScheduler(executor, new AuditLogger());
        using var cts = new CancellationTokenSource();
        var sensorValues = new Dictionary<SensorType, double>();

        var callers = Enumerable.Range(0, 50)
            .Select(_ => scheduler.ScheduleStagesAsync(new[] { StageId.Stage1 }, sensorValues, cts.Token));
        await Task.WhenAll(callers);

        Assert.True(
            await executor.WaitForCompletionsAsync(1, WaitCeiling),
            "expected the one allowed execution to complete");

        Assert.Equal(1, executor.MaxConcurrent);
        Assert.True(executor.TotalStarts >= 1);
    }

    [Fact]
    public async Task ScheduleStagesAsync_WithAlreadyCancelledToken_StillReleasesTheRunningSlot()
    {
        // Task.Run(delegate, ct) with an already-cancelled ct skips the delegate
        // body entirely (verified separately against the BCL) - which would skip
        // the scheduler's `finally { _running.TryRemove(...) }` too and leave this
        // stage permanently unschedulable. A later call with a fresh token must
        // still be able to claim and run the same stage.
        var executor = new TrackingStageExecutor();
        IStageScheduler scheduler = new StageScheduler(executor, new AuditLogger());
        var sensorValues = new Dictionary<SensorType, double>();

        using (var cancelledCts = new CancellationTokenSource())
        {
            cancelledCts.Cancel();
            await scheduler.ScheduleStagesAsync(new[] { StageId.Stage1 }, sensorValues, cancelledCts.Token);
        }

        Assert.True(
            await executor.WaitForCompletionsAsync(1, WaitCeiling),
            "expected the cancelled attempt's finally to run");

        using (var freshCts = new CancellationTokenSource())
        {
            await scheduler.ScheduleStagesAsync(new[] { StageId.Stage1 }, sensorValues, freshCts.Token);
        }

        Assert.True(
            await executor.WaitForCompletionsAsync(1, WaitCeiling),
            "expected the second attempt to run");

        // Without the fix this is 0: the first call's slot leaks, so the second
        // call's TryAdd fails and its executor never runs either.
        Assert.Equal(2, executor.TotalStarts);
    }

    [Fact]
    public async Task ScheduleStagesAsync_ExecutorThrows_ReleasesResources_LogsTheFailure_AndFreesTheSlot()
    {
        // The cancellation-mid-execution tests already confirm resources get
        // released for that specific failure mode. This covers the general case:
        // IStageExecutor is meant to be swappable for a real hardware executor
        // (see architecture.md), and a real one can fail with any exception, not
        // just cancellation - StageScheduler's `catch (Exception ex)` branch had
        // no test coverage at all before this.
        var rm = new ResourceManager();
        var executor = new FailingStageExecutor(rm);
        var audit = new SpyAuditLogger();
        IStageScheduler scheduler = new StageScheduler(executor, audit);
        var sensorValues = new Dictionary<SensorType, double>();

        await scheduler.ScheduleStagesAsync(new[] { StageId.Stage1 }, sensorValues, CancellationToken.None);
        Assert.True(await audit.WaitForFailureAsync(WaitCeiling), "expected the first failure to be logged");

        var failure = Assert.Single(audit.Failures);
        Assert.Equal(StageId.Stage1, failure.StageId);
        Assert.IsType<InvalidOperationException>(failure.Exception);

        // Stage1 requires R_A and R_B - the failing executor's lease must still
        // have released both rather than leaving them stuck Busy.
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));

        // The _running slot must be freed too - scheduling Stage1 again must
        // actually reach the executor a second time, not silently no-op.
        await scheduler.ScheduleStagesAsync(new[] { StageId.Stage1 }, sensorValues, CancellationToken.None);
        Assert.True(await audit.WaitForFailureAsync(WaitCeiling), "expected the second failure to be logged");

        Assert.Equal(2, audit.Failures.Count);
    }

    /// <summary>Acquires resources like a real executor, then always fails - simulating
    /// a hardware fault rather than a cancellation, so cleanup on the non-cancellation
    /// exception path can be verified.</summary>
    private sealed class FailingStageExecutor : IStageExecutor
    {
        private readonly IResourceManager _resourceManager;

        public FailingStageExecutor(IResourceManager resourceManager) => _resourceManager = resourceManager;

        public async Task ExecuteAsync(StageDefinition stage, CancellationToken ct)
        {
            using var lease = _resourceManager.Acquire(stage.RequiredResources, TimeSpan.FromSeconds(5), ct);
            await Task.Yield(); // ensure this is a genuine async failure, not a synchronous throw
            throw new InvalidOperationException("Simulated hardware fault");
        }
    }

    private sealed class SpyAuditLogger : IAuditLogger
    {
        public sealed record Failure(StageId StageId, Exception Exception);

        private readonly List<Failure> _failures = new();
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<Failure> Failures
        {
            get { lock (_failures) return _failures.ToList(); }
        }

        public void LogStageScheduled(
            StageId stageId,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            IReadOnlyCollection<ResourceId> requiredResources,
            DateTimeOffset timestamp)
        {
        }

        public void LogStageFailed(StageId stageId, Exception exception, DateTimeOffset timestamp)
        {
            lock (_failures) { _failures.Add(new Failure(stageId, exception)); }
            _signal.Release();
        }

        public void LogRuleEvaluationFailed(
            Exception exception,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            DateTimeOffset timestamp)
        {
        }

        // WaitAsync, not the blocking Wait: a synchronous wait here would tie up a
        // real thread-pool thread inside an async test, and under a full parallel
        // test run that can starve the pool badly enough to blow past even a
        // multi-second timeout (observed directly on a 4-core machine).
        public Task<bool> WaitForFailureAsync(TimeSpan timeout) => _signal.WaitAsync(timeout);
    }

    /// <summary>Signals completion via a semaphore rather than relying on a fixed
    /// Task.Delay in the tests that use this - a fixed delay flakes under the thread
    /// pool contention of a full parallel test run (observed directly: a sibling test
    /// using Task.Delay(200) here failed intermittently before this was added).
    /// WaitForCompletionsAsync uses WaitAsync for the same reason described on
    /// SpyAuditLogger.WaitForFailureAsync above.</summary>
    private sealed class TrackingStageExecutor : IStageExecutor
    {
        private int _current;
        private readonly object _maxLock = new();
        private readonly SemaphoreSlim _completedSignal = new(0);

        public int MaxConcurrent { get; private set; }
        public int TotalStarts;

        public async Task ExecuteAsync(StageDefinition stage, CancellationToken ct)
        {
            Interlocked.Increment(ref TotalStarts);
            var now = Interlocked.Increment(ref _current);
            lock (_maxLock)
            {
                if (now > MaxConcurrent)
                    MaxConcurrent = now;
            }

            try
            {
                await Task.Delay(50, ct);
            }
            finally
            {
                // In a finally so a cancelled delay still signals completion -
                // otherwise a cancelled run would never release the semaphore.
                Interlocked.Decrement(ref _current);
                _completedSignal.Release();
            }
        }

        public async Task<bool> WaitForCompletionsAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var remaining = timeout - sw.Elapsed;
                if (remaining < TimeSpan.Zero || !await _completedSignal.WaitAsync(remaining))
                    return false;
            }
            return true;
        }
    }
}
