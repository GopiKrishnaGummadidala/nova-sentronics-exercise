# Concurrency Demos

Four small, narrated console demonstrations of the concurrency bugs (and the
fix) discussed in [docs/concurrency.md](../../docs/concurrency.md) — one for
each bug category the exercise names (deadlock, atomicity violation,
order violation), plus the real fix working under the same kind of
contention. Run them all with:

```bash
dotnet run --project tests/NovaExercise.ConcurrencyDemos/NovaExercise.ConcurrencyDemos.csproj
```

Each one is a standalone class in [`Demos/`](Demos); `Program.cs` just runs
them in sequence.

## Demo 1 — Deadlock ([`DeadlockDemo.cs`](Demos/DeadlockDemo.cs))

Two threads acquire `{R_A, R_B}` and `{R_B, R_A}` through
`ResourceManagerNaive`, which — unlike the real `ResourceManager` — holds
each lock while waiting for the next one. Classic hold-and-wait plus
circular-wait: both threads genuinely block for the full ~5s acquisition
timeout before failing with `TimeoutException`. That timeout only exists as
a safety valve for this demo; with `Monitor.Enter` instead of
`Monitor.TryEnter`, this would hang forever.

## Demo 2 — Atomicity Violation ([`AtomicityRaceDemo.cs`](Demos/AtomicityRaceDemo.cs))

Two threads call `NaiveStageTracker.TryStart("stage_1")` at (almost) the
same time. `TryStart` checks a dictionary and writes to it as two separate
steps — not atomic together — so both calls can see "not running" before
either records that it now is. This is the real bug
`StageScheduler.ScheduleStagesAsync` had before it was fixed with a single
atomic `TryAdd`; `NaiveStageTracker` reproduces the same shape in isolation,
widened with an artificial delay so it races on every run instead of
needing thousands of attempts.

## Demo 3 — Order Violation ([`OrderViolationDemo.cs`](Demos/OrderViolationDemo.cs))

One thread calls `NaiveLazyConfig.Initialize()`, which takes a moment; another
calls `GetSetting()` with no wait and no synchronization between them —
nothing enforces that initialization must finish first. The worker reliably
reads the not-yet-published value before the initializer finishes, the same
shape as `concurrency.md`'s illustrative "resources / isReady" example, made
deterministic the same way the other two demos are: an artificial delay
widens the window instead of leaving it to timing luck. Unlike
`ResourceManagerNaive` and `NaiveStageTracker`, `NaiveLazyConfig` doesn't
mirror a bug this codebase's real components ever had — `RuleEngine` and
`SensorRegistry` were always properly ordered — so it lives in this project
rather than `NovaExercise.Core`; see [Design Rationale](../../docs/design-rationale.md).

## Demo 4 — Resource Contention, Handled Correctly ([`ResourceContentionDemo.cs`](Demos/ResourceContentionDemo.cs))

The other three demos show naive code breaking. This one shows the real,
fixed `ResourceManager` succeeding under the same kind of contention: three
stages (`stage1={A,B}`, `stage2={C,B}`, `stage3={A,C}` — the same A-B-C-A
cycle that deadlocks Demo 1) race for overlapping resources across a few
rounds. Watch the console output: only one stage ever holds resources at a
time, everything serializes cleanly, and nothing hangs. The demo tracks
this itself and prints `VERIFIED: ...` if no overlap ever occurred.

## Why these are real classes, not comments

`ResourceManagerNaive` and `NaiveStageTracker` (in `src/NovaExercise.Core`)
and `NaiveLazyConfig` (in this project) are each fully working, independently
runnable implementations of their respective bugs, not code comments
describing them. See [Design Rationale](../../docs/design-rationale.md) for
why, and for why `NaiveLazyConfig` specifically lives here rather than in
`NovaExercise.Core`.
