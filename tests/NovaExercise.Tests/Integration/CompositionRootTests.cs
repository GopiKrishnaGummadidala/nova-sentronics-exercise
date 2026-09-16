using Microsoft.Extensions.DependencyInjection;
using NovaExercise.App;
using NovaExercise.Core.Engine;

namespace NovaExercise.Tests.Integration;

public class CompositionRootTests
{
    [Fact]
    public void AddNovaExerciseServices_ResolvesIRuleEngine_WithNoMissingRegistrations()
    {
        // Guards against the class of bug that's invisible until you actually run
        // the app: a constructor gains a new dependency but nobody registers it,
        // and BuildServiceProvider/GetRequiredService only fails at resolution time.
        var services = new ServiceCollection();
        services.AddNovaExerciseServices();

        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<IRuleEngine>();

        Assert.NotNull(engine);
    }

    [Fact]
    public void AddNovaExerciseServices_ResolvesIRuleEngine_AsASingleton()
    {
        // RuleEngine holds the running system's state (subscribed sensors,
        // in-flight cancellation); resolving two different instances from what
        // should be one composition root would mean two independent engines
        // silently racing over the same sensors.
        var services = new ServiceCollection();
        services.AddNovaExerciseServices();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRuleEngine>();
        var second = provider.GetRequiredService<IRuleEngine>();

        Assert.Same(first, second);
    }
}
