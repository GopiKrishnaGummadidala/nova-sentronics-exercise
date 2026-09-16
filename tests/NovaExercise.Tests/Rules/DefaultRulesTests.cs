using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Rules;

public class DefaultRulesTests
{
    [Fact]
    public void Evaluate_UnionPolicy_ReturnsExpectedStages()
    {
        var rules = DefaultRules.Create();
        IRuleEvaluationPolicy policy = new UnionRuleEvaluationPolicy();

        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 25,
            [SensorType.Pressure] = 40
        };

        var stages = policy.Evaluate(rules, values);

        var expected = new HashSet<StageId>
        {
            StageId.Stage1,
            StageId.Stage2,
            StageId.Stage3
        };

        // Convert result to HashSet for order-independent comparison
        var actual = new HashSet<StageId>(stages);

        Assert.Equal(expected, actual);
    }
}