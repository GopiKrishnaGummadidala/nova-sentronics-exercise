using NovaExercise.Core.Resources;

namespace NovaExercise.Core.Stages;

public sealed class StageDefinition
{
    public StageId Id { get; }
    public IReadOnlyCollection<ResourceId> RequiredResources { get; }

    public StageDefinition(StageId id, IReadOnlyCollection<ResourceId> requiredResources)
    {
        Id = id;
        RequiredResources = requiredResources;
    }
}