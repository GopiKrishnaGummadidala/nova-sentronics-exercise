using System.Collections.Concurrent;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Core.Engine;

public sealed class RuleEngine : IRuleEngine, IDisposable
{
    private readonly ISensorRegistry _sensors;
    private readonly IReadOnlyList<StageRule> _rules;
    private readonly IRuleEvaluationPolicy _policy;
    private readonly IStageScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<SensorType, double> _currentValues = new();

    public RuleEngine(
        ISensorRegistry sensors,
        IReadOnlyList<StageRule> rules,
        IRuleEvaluationPolicy policy,
        IStageScheduler scheduler)
    {
        _sensors = sensors;
        _rules = rules;
        _policy = policy;
        _scheduler = scheduler;

        foreach (var sensor in _sensors.Sensors)
        {
            sensor.ReadingChanged += OnReadingChanged;
            if (sensor.CurrentReading is not null)
                _currentValues[sensor.Type] = sensor.CurrentReading.Value;
        }
    }

    private void OnReadingChanged(SensorReading reading)
    {
        _currentValues[reading.Type] = reading.Value;
        _ = EvaluateAndScheduleAsync();
    }

    private async Task EvaluateAndScheduleAsync()
    {
        var values = _currentValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var stages = _policy.Evaluate(_rules, values);
        await _scheduler.ScheduleStagesAsync(stages, values, _cts.Token);
    }

    public void Start()
    {
        // Driven by sensor events
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        foreach (var sensor in _sensors.Sensors)
            sensor.ReadingChanged -= OnReadingChanged;
    }
}