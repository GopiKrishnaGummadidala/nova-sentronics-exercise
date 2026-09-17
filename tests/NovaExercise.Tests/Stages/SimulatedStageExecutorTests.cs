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
        cts.CancelAfter(TimeSpan.FromMilliseconds(50)); // fires during the 500ms simulated work

        // ThrowsAnyAsync, not ThrowsAsync: Task.Delay(ms, ct) throws
        // TaskCanceledException specifically, a subclass of OperationCanceledException -
        // callers only care that it's *some* OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(stage, cts.Token));

        // `using var lease` inside ExecuteAsync must still Dispose() even though
        // an exception propagated out of the method - a cancelled stage must not
        // leave its resources stuck Busy forever.
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));
    }
}
