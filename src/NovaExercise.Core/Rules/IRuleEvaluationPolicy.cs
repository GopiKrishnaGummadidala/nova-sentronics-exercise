using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Rules;

public interface IRuleEvaluationPolicy
{
    IReadOnlyCollection<StageId> Evaluate(
        IReadOnlyList<StageRule> rules,
        IReadOnlyDictionary<SensorType, double> sensorValues
    );
}