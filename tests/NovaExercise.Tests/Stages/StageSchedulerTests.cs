using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Stages;

public class StageSchedulerTests
{
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

        await Task.Delay(300); // let any in-flight executions finish

        Assert.Equal(1, executor.MaxConcurrent);
        Assert.True(executor.TotalStarts >= 1);
    }

    private sealed class TrackingStageExecutor : IStageExecutor
    {
        private int _current;
        private readonly object _maxLock = new();

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

            await Task.Delay(50, ct);

            Interlocked.Decrement(ref _current);
        }
    }
}