using NovaExercise.Core.Resources;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Stages;

public class StageSchedulerTests
{
    [Fact]
    public async Task ScheduleStagesAsync_DoesNotThrowForValidStages()
    {
        var rm = new ResourceManager();
        IStageExecutor executor = new SimulatedStageExecutor(rm);
        IStageScheduler scheduler = new StageScheduler(executor);

        var stages = new[] { StageId.Stage1, StageId.Stage2 };

        var cts = new CancellationTokenSource();

        // Should not throw
        await scheduler.ScheduleStagesAsync(stages, cts.Token);

        // Give some time for background tasks to start
        await Task.Delay(200);

        cts.Cancel();
    }
}