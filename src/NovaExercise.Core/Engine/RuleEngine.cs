using System.Collections.Concurrent;
using NovaExercise.Core.Rules;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;

namespace NovaExercise.Core.Engine;

public sealed class RuleEngine : IRuleEngine, IDisposable
{
    private readonly ISensorRegistry _sensors;
    private readonly IReadOnlyList<StageRule> _rules;
    private readonly IRuleEvaluationPolicy _policy;
    private readonly IStageScheduler _scheduler;
    private readonly IAuditLogger _audit;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<SensorType, double> _currentValues = new();

    public RuleEngine(
        ISensorRegistry sensors,
        IReadOnlyList<StageRule> rules,
        IRuleEvaluationPolicy policy,
        IStageScheduler scheduler,
        IAuditLogger audit)
    {
        _sensors = sensors;
        _rules = rules;
        _policy = policy;
        _scheduler = scheduler;
        _audit = audit;

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
        foreach (var stageId in stages)
        {
            var def = GetStageDefinition(stageId);
            _audit.LogStageScheduled(stageId, values, def.RequiredResources, DateTimeOffset.Now);
        }
        await _scheduler.ScheduleStagesAsync(stages, _cts.Token);
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

    private static StageDefinition GetStageDefinition(StageId id)
    {
        return id switch
        {
            StageId.Stage1 => new StageDefinition(StageId.Stage1,
                new[] { ResourceId.R_A, ResourceId.R_B }),
            StageId.Stage2 => new StageDefinition(StageId.Stage2,
                new[] { ResourceId.R_C, ResourceId.R_B }),
            StageId.Stage3 => new StageDefinition(StageId.Stage3,
                new[] { ResourceId.R_A, ResourceId.R_C }),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, null)
        };
    }
}