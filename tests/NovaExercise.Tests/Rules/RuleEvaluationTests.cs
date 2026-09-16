using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Rules;

public class RuleEvaluationTests
{
    private readonly IRuleEvaluationPolicy _policy = new UnionRuleEvaluationPolicy();
    private readonly IReadOnlyList<StageRule> _rules = DefaultRules.Create();

    [Fact]
    public void Evaluate_NoRulesMatch_ReturnsEmpty()
    {
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 1,
            [SensorType.Pressure] = 200
        };

        var stages = _policy.Evaluate(_rules, values);

        Assert.Empty(stages);
    }

    [Fact]
    public void Evaluate_OnlyRule1Matches()
    {
        // T > 10 && P < 100
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 15,
            [SensorType.Pressure] = 80
        };

        var stages = _policy.Evaluate(_rules, values);
        var actual = new HashSet<StageId>(stages);

        var expected = new HashSet<StageId> { StageId.Stage1, StageId.Stage2 };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_OnlyRule2Matches()
    {
        // T > 5 && P < 50
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 8,
            [SensorType.Pressure] = 30
        };

        var stages = _policy.Evaluate(_rules, values);
        var actual = new HashSet<StageId>(stages);

        var expected = new HashSet<StageId> { StageId.Stage3, StageId.Stage2 };

        Assert.Equal(expected, actual);
    }

   [Fact]
    public void Evaluate_Rule3Matches_AlsoMatchesRule1_ReturnsUnion()
    {
        // T > 20 && P < 100 matches rule 3 and also rule 1 (since T > 10 && P < 100).
        // So the result is union of {Stage1, Stage3} and {Stage1, Stage2} = {Stage1, Stage2, Stage3}.
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 25,
            [SensorType.Pressure] = 90
        };

        var stages = _policy.Evaluate(_rules, values);
        var actual = new HashSet<StageId>(stages);

        var expected = new HashSet<StageId>
        {
            StageId.Stage1,
            StageId.Stage2,
            StageId.Stage3
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_AllRulesMatch_ReturnsUnionOfAllStages()
    {
        // T = 25, P = 40 matches all three rules
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 25,
            [SensorType.Pressure] = 40
        };

        var stages = _policy.Evaluate(_rules, values);
        var actual = new HashSet<StageId>(stages);

        var expected = new HashSet<StageId>
        {
            StageId.Stage1,
            StageId.Stage2,
            StageId.Stage3
        };

        Assert.Equal(expected, actual);
    }
}