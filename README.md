# Nova Sentronics Exercise – Manufacturing Control System

This repository contains a reference implementation for the Nova Sentronics Senior Software Engineer exercise. It simulates control software for an industrial manufacturing machine that executes production stages based on sensor readings.

## Features

- Simulated temperature and pressure sensors updating every 100 ms.
- Rule engine that evaluates sensor-based conditions and schedules production stages.
- Resource management for three shared resources (R_A, R_B, R_C) with:
  - States: Idle, Busy, Error
  - Deadlock‑free acquisition by construction: resources are claimed atomically one at a time and rolled back on partial failure, so a request never holds one resource while waiting on another (see [docs/concurrency.md](docs/concurrency.md)).
- Extensible design:
  - New sensors can be added via `ISensor`.
  - New rules can be added via `StageRule`.
  - Pluggable rule evaluation policies (e.g., union vs priority).

## Solution Structure

- `src/NovaExercise.Core` – Domain models, interfaces, and core logic.
- `src/NovaExercise.App` – Console application wiring everything together.
- `tests/NovaExercise.Tests` – Unit and integration tests.
- `tests/NovaExercise.ConcurrencyDemos` – Demo project showing a deadlock‑prone resource manager.

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

This runs unit tests for rules, resources, stages, and an end‑to‑end workflow test.

## Concurrency Demo

The `NovaExercise.ConcurrencyDemos` project demonstrates how a naive resource manager can deadlock.

```bash
dotnet run --project tests/NovaExercise.ConcurrencyDemos/NovaExercise.ConcurrencyDemos.csproj
```

> **Warning:** This demo genuinely deadlocks by design — both threads block for the full ~5s acquisition timeout before failing. It is for illustration only.

## Documentation

- [Business Case](docs/business-case.md) — the original exercise brief this repository was built against
- [Architecture](docs/architecture.md)
- [Concurrency Design](docs/concurrency.md)
- [Assumptions](docs/assumptions.md)
- [Design Rationale](docs/design-rationale.md) — why each key decision was made, and what it was chosen over
