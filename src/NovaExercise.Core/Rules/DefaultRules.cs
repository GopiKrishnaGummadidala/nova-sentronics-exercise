using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Rules;

public static class DefaultRules
{
    public static IReadOnlyList<StageRule> Create()
    {
        return new List<StageRule>
        {
            new StageRule(
                values => values.GetValueOrDefault(SensorType.Temperature, 0) > 10.0
                          && values.GetValueOrDefault(SensorType.Pressure, 0) < 100,
                new[] { StageId.Stage1, StageId.Stage2 }
            ),

            new StageRule(
                values => values.GetValueOrDefault(SensorType.Temperature, 0) > 5.0
                          && values.GetValueOrDefault(SensorType.Pressure, 0) < 50,
                new[] { StageId.Stage3, StageId.Stage2 }
            ),

            new StageRule(
                values => values.GetValueOrDefault(SensorType.Temperature, 0) > 20.0
                          && values.GetValueOrDefault(SensorType.Pressure, 0) < 100,
                new[] { StageId.Stage1, StageId.Stage3 }
            ),
        };
    }
}