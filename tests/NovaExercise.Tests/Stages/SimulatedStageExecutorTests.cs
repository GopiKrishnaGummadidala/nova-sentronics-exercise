using NovaExercise.Core.Resources;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Stages;

public class SimulatedStageExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CancelledMidExecution_StillReleasesAcquiredResources()
    {
        var rm = new ResourceManager();
        IStageExecutor executor = new SimulatedStageExecutor(rm);
        var stage = new StageDefinition(StageId.Stage1, new[] { ResourceId.R_A, ResourceId.R_B });

        using var cts = new CancellationTokenSource();

        // No artificial delay, and no CancelAfter: both are timer-based, and under
        // the thread-pool contention of a full parallel test run, two independent
        // timers race unpredictably regardless of their nominal durations - a
        // CancelAfter(50ms) racing this method's Task.Delay(500, ct) was observed
        // losing that race outright under load, and a Task.Delay(100)-then-cancel
        // replacement still relies on the same timer infrastructure being prompt.
        // Acquire() on free resources needs no polling at all, so it completes
        // fully synchronously - by the time ExecuteAsync returns a Task here,
        // its async state machine is *guaranteed* (by the language, not by timing)
        // to already be parked at its only await, Task.Delay(500, ct). Cancelling
        // immediately afterward is therefore not a race at all.
        var executeTask = executor.ExecuteAsync(stage, cts.Token);
        cts.Cancel();

        // ThrowsAnyAsync, not ThrowsAsync: Task.Delay(ms, ct) throws
        // TaskCanceledException specifically, a subclass of OperationCanceledException -
        // callers only care that it's *some* OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);

        // `using var lease` inside ExecuteAsync must still Dispose() even though
        // an exception propagated out of the method - a cancelled stage must not
        // leave its resources stuck Busy forever.
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));
    }
}
