# Concurrency Design

This document explains how the solution handles concurrency, prevents deadlocks, and avoids common concurrency bugs.

## Shared State & Concurrency Hotspots

The main concurrency concerns are:

- **Resource state** (Idle/Busy/Error) in `Resource` and `ResourceManager`.
- **Stage scheduling** in `StageScheduler` (tracking running stages).
- **Sensor readings** in `RuleEngine` (current sensor values dictionary).
- **Sensor subscriptions** in `RuleEngine` (which `SensorType`s it's currently
  wired to, guarded by `_subscriptionLock` so a sensor registered concurrently
  with construction can't be subscribed twice — see
  [Design Rationale](design-rationale.md)).
- **Sensor registration** in `SensorRegistry` (swapping in a replacement for
  an existing `SensorType`), guarded by its own lock so the sensor it's
  replacing can be captured atomically together with storing the new one.

All shared mutable state is protected using locks or concurrent collections.

## Deadlock Prevention

### Resource Acquisition Strategy

`ResourceManager` avoids one of the four necessary conditions for deadlock —
**hold‑and‑wait** — entirely: it never blocks on one resource while holding
another. Each resource is claimed with a single atomic
`TryMarkBusy()` (idle‑check + busy‑flag in one locked step); if a resource is
transiently Busy it polls with a *shared* timeout budget across the whole
request, and if a resource is unavailable when the budget runs out, every
resource already claimed for this request is released before the exception
propagates. Because no lock is ever held across a wait for a different
resource, circular wait cannot form, regardless of acquisition order.

Key points:

- Resources are still sorted by `ResourceId` before acquiring them. This is
  defense‑in‑depth (keeps contention patterns deterministic across callers
  requesting overlapping sets) rather than the actual deadlock‑freedom
  mechanism — the hold‑and‑wait‑free design above is what matters.
- The acquisition timeout is a single deadline shared across all requested
  resources, not re‑applied in full to each one.
- If any required resource cannot be claimed, all previously‑claimed
  resources for that request are released before the exception propagates.

### Deadlock‑Prone Alternative: `ResourceManagerNaive`

`ResourceManagerNaive` locks resources in the **order requested** (not a
global order) and, unlike `ResourceManager`, **holds each lock while waiting
for the next one** — genuine hold‑and‑wait.

**Example scenario:**

- Thread 1: acquire `{R_A, R_B}`
  - Locks R_A, holds it, blocks trying to lock R_B.
- Thread 2: acquire `{R_B, R_A}`
  - Locks R_B, holds it, blocks trying to lock R_A.

Result:

- Thread 1 holds R_A, waits for R_B.
- Thread 2 holds R_B, waits for R_A.
- Circular wait → real deadlock: both threads are genuinely blocked at the
  same time, each holding what the other needs.

This is demonstrated in `tests/NovaExercise.ConcurrencyDemos`, and verified by
running it: both threads block for the full acquisition timeout (~5s) before
failing with `TimeoutException`. The timeout is a deliberate safety valve for
running this as an unattended demo — with `Monitor.Enter` (no timeout) in
place of `Monitor.TryEnter`, the same code would hang forever, which is the
"may hang" behavior this class originally described in comments but did not
actually exhibit (see note below).

> **Note on an earlier version of this demo:** the naive manager originally
> released each resource's lock before moving on to the next one, so no lock
> was ever held while waiting for another — meaning it could never actually
> deadlock. Two racing threads would instead fail near‑instantly with
> `InvalidOperationException` (a busy‑state race, not a hang). That was itself
> a live example of an unintended atomicity violation standing in for the
> deadlock the class was meant to demonstrate. The current version fixes this
> by holding each lock for the duration of the whole acquisition.

### Why the Stage Map Encourages Deadlock

Stage resource requirements:

- `stage_1`: {R_A, R_B}
- `stage_2`: {R_C, R_B}
- `stage_3`: {R_A, R_C}

These form a cycle (A–B–C–A), which is a classic setup for deadlock if locks are taken in inconsistent orders. The global ordering in `ResourceManager` explicitly prevents this.

## Non‑Deadlock Concurrency Bugs

### Atomicity‑Violation Example (found in this codebase, not just illustrative)

`StageScheduler.ScheduleStagesAsync` originally did a check‑then‑act on a
`ConcurrentDictionary` that tracks in‑flight stages:

```csharp
// BUGGY: ContainsKey (check) and the later indexer assignment (act) are two
// separate operations - not atomic together, even though each one alone is
// thread-safe on a ConcurrentDictionary.
if (_running.ContainsKey(id))
    continue;
...
_running[id] = task; // a second thread can reach here before this line runs
```

Because `RuleEngine` reacts to sensor events, and the Temperature and Pressure
sensors tick on two independent threads, two calls to `ScheduleStagesAsync`
for the *same* stage can happen within microseconds of each other. Both can
pass the `ContainsKey` check before either writes to the dictionary, so both
start a `Task.Run` for the same stage — violating the "at most one instance
of a given stage runs at a time" invariant (see
[assumptions.md](assumptions.md)). This was verified with a standalone
200,000‑iteration repro of the same check‑then‑act shape (5 double‑passes
observed), and empirically in this app: with the race in place, the log
would show the same stage "scheduled" twice at the same timestamp whenever
both sensors ticked close together. The same shape is reproduced as a
runnable, deterministic demo in `NovaExercise.ConcurrencyDemos`
(`NaiveStageTracker`) — an artificial delay between the check and the write
widens the race window the same way `ResourceManagerNaive`'s does for the
deadlock demo, so it reproduces on every run instead of needing 200,000
attempts to get lucky.

**How we fixed it:** replace the check‑then‑act with a single atomic
operation, `_running.TryAdd(id, ...)`. `TryAdd` either claims the slot
exclusively or fails if another caller already owns it — there is no window
between checking and acting because there is only one call.

### Reservation‑Lifecycle Race (found in independent review after the fix above)

The `TryAdd` fix closed the check‑then‑act window, but left a second, subtler
one: the reserved dictionary *value* was still `Task.Run`'s own returned
handle, written back **after** `Task.Run` was called:

```csharp
if (!_running.TryAdd(id, Task.CompletedTask))
    continue;
var task = Task.Run(async () => { /* ... finally { _running.TryRemove(id, out _); } */ });
_running[id] = task; // <- Task.Run already returned; the worker may have too
```

`Task.Run` hands the caller a `Task` handle only *after* queuing the work —
it does not wait for the worker thread to start, let alone finish. If the
executor completes synchronously (no real `await` suspension — true for
`Task.CompletedTask`, and therefore for any executor, including a test
double, that never genuinely yields), the worker thread can run the whole
delegate — including its own `finally { _running.TryRemove(id, out _) }` —
before the scheduling thread reaches the `_running[id] = task` line above.
That line then reinserts a now‑stale entry that nothing will ever remove
again, permanently blocking that stage.

Every executor already in this codebase (`SimulatedStageExecutor`,
`TrackingStageExecutor`, `FailingStageExecutor`) does a real `Task.Delay` or
`Task.Yield`, which always yields — so this specific race could not be
triggered through any of them, and slipped past the `TryAdd` fix and its
own regression test undetected. It needed a purpose‑built
synchronously‑completing executor to expose: verified directly, the stage
got permanently stuck after just 2 of 200,000 rapid, unpaced scheduling
attempts.

**How we fixed it:** reserve the *final* dictionary value up front instead
of overwriting it after the fact. A `TaskCompletionSource` is created and
`TryAdd`‑ed before any work starts; the actual execution — including
`Task.Run` — happens afterward, and the same `TaskCompletionSource` is what
gets removed from `_running` and completed in the `finally`. `TryAdd` and
`TryRemove` are now the *only* two places that ever touch a stage's entry,
with nothing in between that could race.

### Cancellation Edge Case (also found in this codebase)

`StageScheduler` originally passed `ct` as `Task.Run`'s own cancellation
token, not just to the awaited work inside:

```csharp
var task = Task.Run(async () => { /* ... finally { _running.TryRemove(id, out _); } */ }, ct);
```

Verified against the BCL: if `ct` is *already* cancelled at the moment
`Task.Run` is called, .NET returns an already‑`Canceled` `Task` **without
ever invoking the delegate** — so the `finally` block never runs either.
The stage's slot in `_running` is left permanently occupied, and `TryAdd`
for that stage id fails forever after, blocking it from ever being
scheduled again for the lifetime of that `StageScheduler`. In the current
app this is low‑impact (the whole process exits shortly after `Stop()`),
but it's a real, reachable bug: a sensor reading delivered after
`RuleEngine.Stop()` cancels its token, but before `Dispose()` unsubscribes,
carries an already‑cancelled token straight into this path.

**How we fixed it:** stopped passing `ct` to `Task.Run` itself, so the
delegate body always runs regardless of the token's state; cancellation is
still fully honored via `await _executor.ExecuteAsync(def, ct)` inside,
where the `finally` is guaranteed to execute either way.

### Atomicity‑Violation Example (resource state)

The same bug shape applies to resource state:

```csharp
// BUGGY pseudo-code
if (resource.State == ResourceState.Idle)
{
    resource.State = ResourceState.Busy; // not atomic with the check
    // use resource
}
```

Two threads could both see `Idle` and both transition to `Busy`, violating exclusivity.

**How we avoid it:**

- `Resource.TryMarkBusy()` performs the check and the transition inside a
  single `lock (_lock) { ... }` block, so there is no window between them.
- `ResourceManager.AcquireAsync` only ever calls this one atomic method — it
  never reads `State` and decides separately whether to call a setter.

### Order‑Violation Example

An order‑violation can occur if initialization and usage are not properly synchronized:

```csharp
// Thread 1: initialization
resources = InitializeResources();
isReady = true;

// Thread 2: worker
while (!isReady) Thread.Yield();
Use(resources); // may see partially initialized resources
```

**How we avoid it:**

- Shared objects (e.g., `SensorRegistry`, `ResourceManager`) are constructed before being shared.
- `RuleEngine` subscribes to sensors in its constructor; no background thread uses it before construction completes.
- We rely on .NET’s memory model and proper task scheduling; no unsafe “publish then initialize” patterns are used.

## Testing Concurrency

- `ResourceManagerTests` verify correct behavior under contention (e.g., failed acquisitions release all resources, busy resources cause a `TimeoutException` rather than corrupting state).
- `StageSchedulerTests.ScheduleStagesAsync_ConcurrentCallsForSameStage_NeverRunsMoreThanOneAtOnce` fires 50 concurrent `ScheduleStagesAsync` calls for the same stage and asserts the executor never observes more than one concurrent execution — a regression test for the atomicity violation above.
- `StageSchedulerTests.ScheduleStagesAsync_AfterABurstOfFastCompletions_TheStageIsStillSchedulable` fires 20,000 unpaced `ScheduleStagesAsync` calls against a synchronously-completing executor and asserts the stage can still complete afterward — a regression test for the reservation-lifecycle race above, which none of the other tests could reach since every other executor here genuinely yields.
- `EndToEndWorkflowTests` exercise the full pipeline with concurrent sensor updates and stage executions.
- `NovaExercise.ConcurrencyDemos` provides three manual, genuinely‑reproducing
  demonstrations: deadlock with `ResourceManagerNaive`, the check‑then‑act
  atomicity violation above with `NaiveStageTracker`, and — for contrast —
  the real `ResourceManager` correctly resolving the same kind of resource
  contention across repeated rounds, with no overlap and no deadlock.

## Summary

- Deadlocks are prevented in `ResourceManager` by **never holding one resource while waiting on another** (avoiding hold‑and‑wait), with sorted acquisition order kept as harmless defense‑in‑depth.
- `ResourceManagerNaive` deliberately does the opposite — holds a lock while waiting for the next one — to give a genuine, reproducible deadlock for the exercise's "present at least one deadlock" requirement.
- Atomicity violations are avoided by using single atomic operations (`TryMarkBusy`, `ConcurrentDictionary.TryAdd`) instead of separate check‑then‑act steps — including a real one found and fixed in `StageScheduler` during review, not just the textbook `Resource.State` example. `NaiveStageTracker` reproduces that exact bug on demand, the same way `ResourceManagerNaive` does for the deadlock above.
- Order violations are avoided by **careful construction and publication** of shared objects.
- The design is intentionally simple and explicit to make concurrency reasoning straightforward.
