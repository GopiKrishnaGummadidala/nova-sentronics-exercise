using System.Collections.Concurrent;
using NovaExercise.Core.Logging;
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
    private readonly ISystemLogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<SensorType, double> _currentValues = new();

    // Tracks which SensorTypes are currently subscribed, so SubscribeToSensor is
    // idempotent: the constructor subscribes to registry-change events *before*
    // snapshotting the current sensor list, so a sensor registered in that gap
    // would otherwise fire the event and also appear in the snapshot, subscribing
    // OnReadingChanged to it twice - double-processing every one of its readings.
    private readonly object _subscriptionLock = new();
    private readonly HashSet<SensorType> _subscribedTypes = new();

    public RuleEngine(
        ISensorRegistry sensors,
        IReadOnlyList<StageRule> rules,
        IRuleEvaluationPolicy policy,
        IStageScheduler scheduler,
        ISystemLogger logger)
    {
        _sensors = sensors;
        _rules = rules;
        _policy = policy;
        _scheduler = scheduler;
        _logger = logger;

        _sensors.SensorRegistered += OnSensorRegistered;
        _sensors.SensorUnregistered += OnSensorUnregistered;

        foreach (var sensor in _sensors.Sensors)
        {
            SubscribeToSensor(sensor);
        }
    }

    private void OnSensorRegistered(ISensor sensor) => SubscribeToSensor(sensor);

    private void OnSensorUnregistered(ISensor sensor)
    {
        lock (_subscriptionLock)
        {
            if (!_subscribedTypes.Remove(sensor.Type))
                return; // never subscribed, or already unsubscribed
        }

        sensor.ReadingChanged -= OnReadingChanged;
        _currentValues.TryRemove(sensor.Type, out _);
    }

    private void SubscribeToSensor(ISensor sensor)
    {
        lock (_subscriptionLock)
        {
            if (!_subscribedTypes.Add(sensor.Type))
                return; // already subscribed - see the field comment above
        }

        sensor.ReadingChanged += OnReadingChanged;
        if (sensor.CurrentReading is not null)
            _currentValues[sensor.Type] = sensor.CurrentReading.Value;
    }

    private void OnReadingChanged(SensorReading reading)
    {
        _currentValues[reading.Type] = reading.Value;
        _ = EvaluateAndScheduleAsync();
    }

    private async Task EvaluateAndScheduleAsync()
    {
        var values = _currentValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        try
        {
            var stages = _policy.Evaluate(_rules, values);
            await _scheduler.ScheduleStagesAsync(stages, values, _cts.Token);
        }
        catch (Exception ex)
        {
            // EvaluateAndScheduleAsync's returned Task is discarded by the
            // fire-and-forget call in OnReadingChanged, so an exception anywhere
            // in this method - not just rule evaluation - would otherwise fault
            // that Task silently, with no crash and no record. Originally only
            // _policy.Evaluate(...) was wrapped; ScheduleStagesAsync can also
            // throw synchronously (e.g. a rule pointing at a StageId
            // StageScheduler.GetStageDefinition has no case for), and that was
            // escaping uncaught. Rules and stage requirements are both explicit
            // extensibility points, so a bug in either must not be invisible.
            _logger.LogRuleEvaluationFailed(ex, values, DateTimeOffset.Now);
        }
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
        _sensors.SensorRegistered -= OnSensorRegistered;
        _sensors.SensorUnregistered -= OnSensorUnregistered;
        foreach (var sensor in _sensors.Sensors)
            sensor.ReadingChanged -= OnReadingChanged;
    }
}