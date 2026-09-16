using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Rules;

public delegate bool SensorPredicate(IReadOnlyDictionary<SensorType, double> values);

public sealed class StageRule
{
    public SensorPredicate Condition { get; }
    public IReadOnlyCollection<StageId> Stages { get; }

    public StageRule(SensorPredicate condition, IReadOnlyCollection<StageId> stages)
    {
        Condition = condition;
        Stages = stages;
    }
}