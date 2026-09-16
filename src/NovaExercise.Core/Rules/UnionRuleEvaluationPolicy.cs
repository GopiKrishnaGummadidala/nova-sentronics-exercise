using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Rules;

public sealed class UnionRuleEvaluationPolicy : IRuleEvaluationPolicy
{
    public IReadOnlyCollection<StageId> Evaluate(
        IReadOnlyList<StageRule> rules,
        IReadOnlyDictionary<SensorType, double> sensorValues)
    {
        var result = new HashSet<StageId>();
        foreach (var rule in rules)
        {
            if (rule.Condition(sensorValues))
            {
                foreach (var s in rule.Stages)
                    result.Add(s);
            }
        }
        return result;
    }
}