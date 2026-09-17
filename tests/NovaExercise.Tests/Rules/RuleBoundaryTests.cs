using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Rules;

/// <summary>
/// All three DefaultRules use strict inequalities (&gt;, &lt;), and their stage
/// sets overlap - Rule1={Stage1,Stage2}, Rule2={Stage3,Stage2}, Rule3={Stage1,Stage3} -
/// so testing "does the boundary work" by only checking the resulting stage set can
/// hide a wrong rule firing behind a right one that was already going to fire anyway.
/// Each case here picks Pressure (or Temperature) specifically to suppress every rule
/// except the one whose boundary is under test, so the expected stage set can only be
/// explained by that one rule's condition, not a coincidental union.
/// </summary>
public class RuleBoundaryTests
{
    private readonly IRuleEvaluationPolicy _policy = new UnionRuleEvaluationPolicy();
    private readonly IReadOnlyList<StageRule> _rules = DefaultRules.Create();

    private static readonly StageId[] None = Array.Empty<StageId>();
    private static readonly StageId[] Rule1 = { StageId.Stage1, StageId.Stage2 };
    private static readonly StageId[] Rule2 = { StageId.Stage3, StageId.Stage2 };
    private static readonly StageId[] AllStages = { StageId.Stage1, StageId.Stage2, StageId.Stage3 };

    public static IEnumerable<object[]> BoundaryCases()
    {
        // Pressure=30 satisfies both P<50 and P<100, so it never gates Rule1/Rule2/Rule3
        // on pressure - only the Temperature threshold under test can flip the result.
        yield return new object[] { 5.0, 30.0, None, "Rule2 lower bound (T>5) is exclusive: exactly 5.0 must not match" };
        yield return new object[] { 5.0001, 30.0, Rule2, "just above Rule2's lower bound must match" };
        yield return new object[] { 10.0, 30.0, Rule2, "Rule1 lower bound (T>10) is exclusive: exactly 10.0 must not add Rule1, only Rule2 (T>5) is active" };
        yield return new object[] { 10.0001, 30.0, AllStages, "just above Rule1's lower bound activates Rule1 alongside the already-active Rule2" };

        // Pressure=70 satisfies P<100 (Rule1, Rule3) but not P<50 (Rule2), isolating
        // Rule3's upper temperature threshold from Rule2 entirely.
        yield return new object[] { 20.0, 70.0, Rule1, "Rule3 lower bound (T>20) is exclusive: exactly 20.0 must not add Stage3" };
        yield return new object[] { 20.0001, 70.0, AllStages, "just above Rule3's lower bound activates Rule3 (Stage3) alongside Rule1" };

        // Temperature=25 satisfies all three rules' temperature conditions, isolating
        // the shared upper Pressure threshold (P<100) used by Rule1 and Rule3.
        yield return new object[] { 25.0, 100.0, None, "Rule1 and Rule3's upper bound (P<100) is exclusive: exactly 100.0 must match neither" };
        yield return new object[] { 25.0, 99.9999, AllStages, "just below the P<100 bound activates both Rule1 and Rule3" };

        // Temperature=7 satisfies only Rule2's condition (T>5, not T>10 or T>20),
        // isolating Rule2's own upper Pressure threshold (P<50).
        yield return new object[] { 7.0, 50.0, None, "Rule2's upper bound (P<50) is exclusive: exactly 50.0 must not match" };
        yield return new object[] { 7.0, 49.9999, Rule2, "just below Rule2's P<50 bound activates it" };
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Evaluate_AtInequalityBoundary_MatchesExpectedStagesOnly(
        double temperature, double pressure, StageId[] expectedStages, string because)
    {
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = temperature,
            [SensorType.Pressure] = pressure
        };

        var stages = _policy.Evaluate(_rules, values);

        Assert.True(
            new HashSet<StageId>(expectedStages).SetEquals(stages),
            $"{because} (temperature={temperature}, pressure={pressure}, got=[{string.Join(",", stages)}])");
    }

    [Fact]
    public void Evaluate_MissingPressureReading_DefaultsToZero_WhichSatisfiesBothUpperBounds()
    {
        // GetValueOrDefault(SensorType.Pressure, 0) means a sensor that has never
        // reported yet is silently treated as Pressure=0 - which trivially satisfies
        // every "< N" condition in this rule set. Documented here as an explicit,
        // intentional-looking assumption rather than a silent trap: a stage can fire
        // on Temperature alone before the Pressure sensor has ever produced a reading.
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 25.0
            // Pressure intentionally omitted.
        };

        var stages = _policy.Evaluate(_rules, values);
        var actual = new HashSet<StageId>(stages);

        Assert.Equal(new HashSet<StageId>(AllStages), actual);
    }

    [Fact]
    public void Evaluate_MissingTemperatureReading_DefaultsToZero_WhichSatisfiesNoRule()
    {
        // The mirror image of the case above: every rule requires Temperature above
        // some positive threshold, so defaulting a missing reading to 0 happens to
        // fail closed here rather than open. Both defaults come from the same
        // GetValueOrDefault(..., 0) call in DefaultRules - it's a coincidence of
        // these particular thresholds, not a designed-in safety property.
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Pressure] = 30.0
            // Temperature intentionally omitted.
        };

        var stages = _policy.Evaluate(_rules, values);

        Assert.Empty(stages);
    }

    [Fact]
    public void Evaluate_NaNTemperature_MatchesNoRule()
    {
        // Every comparison against NaN is false in .NET (NaN > 5 is false, NaN < 100
        // is false), so a malfunctioning sensor reporting NaN silently matches nothing
        // rather than throwing or matching everything. Worth pinning down explicitly
        // for a system whose whole job is reacting to physical sensor readings.
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = double.NaN,
            [SensorType.Pressure] = 30.0
        };

        var stages = _policy.Evaluate(_rules, values);

        Assert.Empty(stages);
    }

    [Fact]
    public void Evaluate_NoRulesConfigured_ReturnsEmpty()
    {
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 25.0,
            [SensorType.Pressure] = 30.0
        };

        var stages = _policy.Evaluate(Array.Empty<StageRule>(), values);

        Assert.Empty(stages);
    }
}
