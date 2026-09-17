using NovaExercise.Core.Resources;

namespace NovaExercise.Core.Stages;

public sealed class SimulatedStageExecutor : IStageExecutor
{
    private readonly IResourceManager _resourceManager;

    public SimulatedStageExecutor(IResourceManager resourceManager)
    {
        _resourceManager = resourceManager;
    }

    public async Task ExecuteAsync(StageDefinition stage, CancellationToken ct)
    {
        using var lease = await _resourceManager.AcquireAsync(
            stage.RequiredResources,
            TimeSpan.FromSeconds(5),
            ct
        );

        await Task.Delay(500, ct);
    }
}