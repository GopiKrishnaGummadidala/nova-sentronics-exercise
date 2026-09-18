# Nova Sentronics Exercise – Manufacturing Control System

[![CI](https://github.com/GopiKrishnaGummadidala/nova-sentronics-exercise/actions/workflows/ci.yml/badge.svg)](https://github.com/GopiKrishnaGummadidala/nova-sentronics-exercise/actions/workflows/ci.yml)

This repository contains a reference implementation for the Nova Sentronics Senior Software Engineer exercise. It simulates control software for an industrial manufacturing machine that executes production stages based on sensor readings.

## Features

- Simulated temperature and pressure sensors updating every 100 ms.
- Rule engine that evaluates sensor-based conditions and schedules production stages.
- Resource management for three shared resources (R_A, R_B, R_C) with:
  - States: Idle, Busy, Error
  - Deadlock-free multi-resource acquisition by avoiding hold-and-wait,
    with deterministic resource ordering as defense-in-depth (see [docs/concurrency.md](docs/concurrency.md)).
- Extensible design:
  - New sensors can be added via `ISensor`.
  - New rules can be added via `StageRule`.
  - Pluggable rule evaluation policies (e.g., union vs priority).

## Solution Structure

- `src/NovaExercise.Core` – Domain models, interfaces, and core logic.
- `src/NovaExercise.App` – Console application wiring everything together.
- `tests/NovaExercise.Tests` – Unit and integration tests.
- `tests/NovaExercise.ConcurrencyDemos` – Three narrated demos: a deadlock, a check‑then‑act atomicity violation, and the real `ResourceManager` handling the same contention correctly (see its own [README](tests/NovaExercise.ConcurrencyDemos/README.md)).

## Build & Run

Prerequisites:

- .NET 9 SDK (or compatible .NET 8/7 SDK with minor project file adjustments).

From the repository root:

```bash
dotnet build
dotnet run --project src/NovaExercise.App/NovaExercise.App.csproj
```

The application runs until you press **Ctrl+C**.

## Tests

```bash
dotnet test
```

This runs unit tests for rules (including boundary values at each threshold),
resources, sensor registration/replacement, stage scheduling, and the rule
engine (including its reaction to sensors registered, removed, or replaced at
runtime), plus integration tests covering the DI composition root, an
end‑to‑end sensor‑to‑stage workflow, and concurrent resource contention
across the full stage map.

## Concurrency Demos

The `NovaExercise.ConcurrencyDemos` project runs three narrated demonstrations
back to back: a genuine deadlock, a genuine atomicity violation, and the real
`ResourceManager` correctly resolving the same kind of contention that breaks
the first one. See [its own README](tests/NovaExercise.ConcurrencyDemos/README.md)
for what each one shows.

```bash
dotnet run --project tests/NovaExercise.ConcurrencyDemos/NovaExercise.ConcurrencyDemos.csproj
```

> **Warning:** Demo 1 genuinely deadlocks by design — both threads block for
> the full ~5s acquisition timeout before failing. All three demos use
> artificial delays purely to make their outcomes reproduce deterministically
> on every run, not because any of the underlying behavior needs them to occur.

## Documentation

- [Business Case](docs/business-case.md) — the original exercise brief this repository was built against
- [Architecture](docs/architecture.md)
- [Concurrency Design](docs/concurrency.md)
- [Assumptions](docs/assumptions.md)
- [Design Rationale](docs/design-rationale.md) — why each key decision was made, and what it was chosen over
