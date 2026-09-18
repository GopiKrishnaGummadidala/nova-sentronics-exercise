using Microsoft.Extensions.DependencyInjection;
using NovaExercise.Core.Engine;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.App;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the fixed part of the object graph (everything except the
    /// per-instance-configured sensors - see Program.cs). Extracted out of
    /// Program.cs so a test can build this same registration and assert it
    /// resolves without a missing dependency, instead of only finding that out
    /// by running the app.
    /// </summary>
    public static IServiceCollection AddNovaExerciseServices(this IServiceCollection services)
    {
        services.AddSingleton<ISensorRegistry, SensorRegistry>();
        services.AddSingleton<IResourceManager, ResourceManager>();
        services.AddSingleton<IStageExecutor, SimulatedStageExecutor>();
        services.AddSingleton<IStageScheduler, StageScheduler>();
        services.AddSingleton<IRuleEvaluationPolicy, UnionRuleEvaluationPolicy>();
        services.AddSingleton<ISystemLogger, SystemLogger>();
        services.AddSingleton<IReadOnlyList<StageRule>>(_ => DefaultRules.Create());
        services.AddSingleton<IRuleEngine, RuleEngine>();

        return services;
    }
}
