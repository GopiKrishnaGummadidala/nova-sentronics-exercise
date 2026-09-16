namespace NovaExercise.Core.Stages;

public interface IStageExecutor
{
    Task ExecuteAsync(StageDefinition stage, CancellationToken ct);
}