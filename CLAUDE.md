# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is the simulator repository for the Service Delivery system. It is a **.NET 10 Worker Service** that drives the POC with realistic vehicle data — it is not production code and does not use Clean Architecture. A single well-organized project with clear internal separation is the right pattern here.

## System Context

The simulator is an **external actor** that calls the backend API the same way a real Telematics integration would. The backend does not know or care whether it is receiving data from the simulator or real hardware. When real Telematics is available, replacing the simulator is a configuration change — not a code change.

The simulator (see central repo ADR-0009 for the "Human Takeover" design):
- **Dual authentication** — it logs in as the real seeded rep accounts `rep1…rep8` (one session each, using the shared rep password) to make job decisions on their behalf, and holds one `Simulator`-role account used **only** to post vehicle positions for all trucks. There is no longer a single auto-accepting `simulator@system.internal` service account.
- **Position engine** — drives **every** truck's position from backend job-state and posts updates every **3 seconds** to the backend, regardless of whether a truck is operated by the simulator or by a human who has taken over.
- **Auto-decision engine** — for the reps it operates (non-human reps only), connects each one's SignalR `RepHub`, auto-accepts ~85% of job offers and declines ~15% (configurable), then auto-arrives, works on-site for a randomized **120–240 seconds**, and auto-completes.
- **Reconciliation + yield-on-takeover** — each tick it reads current fleet state, operates only the reps no human controls, and rebalances. When a human takes over a rep it yields that rep permanently for the rest of the run (sticky) and never re-assumes it. Abandoned jobs re-match.
- **Human-truck hold-and-wait** — for a truck a human has taken over, the position engine navigates to the requester after the human Accepts, then holds in place until the human taps Arrived/Complete.

## Required Reading Before Implementing

Read this before writing any code:
- [`docs/simulator-spec.md`](docs/simulator-spec.md) — full simulator specification: routes, position update behavior, job offer handling, authentication, configuration

For the backend contract (API endpoints and SignalR hub events), refer to `docs/api-design.md` in the backend repo.

## Project Structure

```
src/ServiceDelivery.Simulator/
├── Workers/          One VehicleWorker background service per vehicle (8 total)
├── Services/         BackendApiClient (HTTP) and SignalRClient (real-time job offers)
├── Models/           VehicleRoute, RouteWaypoint, VehiclePosition
├── Configuration/    SimulatorOptions — strongly-typed settings from appsettings.json
├── Program.cs        Host bootstrapping — registers services and 8 VehicleWorker instances
└── appsettings.json  Default config (BackendBaseUrl, PositionUpdateIntervalSeconds, etc.)

tests/ServiceDelivery.Simulator.Tests/
└── Mirror of src structure — unit tests for Workers, Services, Models
```

## Implementing Stories

Stories for this repo (`SIM-001` through `SIM-007`) are implemented using the Master agent in `service-delivery-central`. Invoke it with the story ID:

```
/master SIM-003
```

The agent creates a feature branch, runs the full TDD pipeline (evaluate → plan → implement → AI review → PR), and pauses at two human checkpoints. Never implement a story by writing code directly without the agent — TDD discipline and SOLID checks are enforced through that pipeline.

### Audit Files (`.stories/`)

During story execution the agent writes ephemeral working files to `.stories/<STORY-ID>/` in this repo. These files are gitignored and deleted at the start of each new run — they are session-scoped working memory for the pipeline, not source files. Do not create or commit anything under `.stories/`.

## Commands

```bash
# Build
dotnet build ServiceDelivery.Simulator.slnx

# Run the simulator (requires backend running and appsettings.Local.json configured).
# DOTNET_ENVIRONMENT=Local makes Host.CreateDefaultBuilder layer appsettings.Local.json on top.
DOTNET_ENVIRONMENT=Local dotnet run --project src/ServiceDelivery.Simulator

# Run tests
dotnet test ServiceDelivery.Simulator.slnx
```

## Local Configuration

Credentials live only in the gitignored `appsettings.Local.json` — the committed `appsettings.json` carries no secrets (empty password fields). Copy `appsettings.Local.json.example` to `appsettings.Local.json` and fill it in:

```json
{
  "Simulator": {
    "BackendBaseUrl": "http://localhost:5180",
    "SimulatorPassword": "<Simulator-role account password from backend seed data — used only to post vehicle positions>",
    "RepPassword": "<shared password for the seeded rep1…rep8 accounts — used to log in as each rep and make job decisions>"
  }
}
```

`appsettings.Local.json` is loaded only when `DOTNET_ENVIRONMENT=Local` (the `Host.CreateDefaultBuilder` convention — it layers `appsettings.{DOTNET_ENVIRONMENT}.json`). `scripts/local/start.sh` sets this for you. The same pattern is the template for future `appsettings.Development.json` / `appsettings.Test.json` / `appsettings.Production.json`. The backend's local HTTP profile is `http://localhost:5180` (HTTPS is `https://localhost:7256`).

## Key Behaviors

See central repo ADR-0009 for the authoritative "Human Takeover" design.

- **Position updates**: Every 3 seconds the position engine drives every truck from backend job-state and calls `POST /vehicles/{id}/position` using the `Simulator`-role account — including trucks a human has taken over. Position is simulator-pushed, not backend-derived.
- **Route loops**: Idle vehicles traverse ordered Iowa waypoint arrays continuously — when the last waypoint is reached, wrap back to the first
- **Job deviation**: When a job is in flight, the truck navigates straight-line toward the requester's lat/lng, then returns to the nearest loop waypoint on completion
- **Auto-decision (non-human reps)**: For each rep the simulator operates, a random decision per offer using `AutoDeclineRatePercent` — add a 1–5 second delay before responding to simulate a real rep reviewing the offer — then auto-arrive, work on-site for a randomized 120–240 seconds, and auto-complete
- **Reconciliation + yield-on-takeover**: Each tick, read current fleet state and operate only reps no human controls. A rep taken over by a human is yielded permanently for the rest of the run (sticky) and never re-assumed; abandoned jobs re-match. The position engine still drives the human's truck — navigating to the requester after the human Accepts, then holding until the human taps Arrived/Complete
- **Startup sequence**: Authenticate the `Simulator`-role account and the `rep1…rep8` accounts → connect each operated rep's SignalR `RepHub` → start the position engine and per-rep decision loops

## Wire Contract

The simulator deserializes backend responses (`/simulator/fleet-state`, `/vehicles/available`) into typed models. Treat the wire as a contract that must **fail loud** on drift:

- A wire enum that arrives **unmapped, missing, or as an integer must THROW** — never silently bind to a bogus value. `BackendApiClient.FleetStateJsonOptions` uses `new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)` so only valid enum-name strings bind; an integer or an unrecognised name raises a `JsonException`.
- Back **each consumed endpoint with a captured-real-payload contract test** — feed a faithful backend response (camelCase wire shape) through the real `GetFleetStateAsync` / `GetAvailableVehicleIdsAsync` deserialization path (mocked `HttpMessageHandler`), asserting typed fields bind and that drift throws. See `tests/ServiceDelivery.Simulator.Tests/Services/BackendApiClientContractTests.cs`.

Rationale: a silently-bound wrong value is the failure mode behind BUG-016 (object-array `/vehicles/available` parsed as `string[]`) and BUG-036 (enum drift). Per central ADR-0011, the contract is verified against the known/live response shape, captured as a fixture.

## Test-Driven Development

TDD is mandatory in this repo, same as all other repos in the Service Delivery system.

```
Red   → Write a failing test that describes the behaviour you want
Green → Write the minimum production code to make it pass
Refactor → Clean up without breaking the tests
```

Test naming convention: `GivenAVehicleWorker_WhenAssignedAJob_ThenItNavigatesToRequesterLocation`

### Test Structure — Arrange / Act / Assert

Every test must have clearly separated sections:

```csharp
[Fact]
public void GivenAVehicleWorker_WhenJobAccepted_ThenNavigationDeviatesFromLoop()
{
    // Arrange
    var apiClient = new Mock<IBackendApiClient>();
    var worker = new VehicleWorker(0, apiClient.Object, NullLogger<VehicleWorker>.Instance);

    // Act
    worker.AssignJob(new JobAssignment(...));

    // Assert
    Assert.True(worker.HasActiveJob);
}
```

## SOLID Principles

Apply SOLID within the single project:
- **S** — `VehicleWorker` moves vehicles; `BackendApiClient` handles HTTP; `SignalRClient` handles real-time. No class does more than one thing.
- **O** — Add new behaviors (e.g. more realistic routing) by extending, not modifying existing workers/services.
- **L** — `BackendApiClient` and `SignalRClient` must fully implement their interfaces — no silent no-ops or partial implementations in production code. During active development, `TODO` comments with `throw new NotImplementedException()` are acceptable scaffolding, but every `NotImplementedException` must be resolved before the simulator can run end-to-end.
- **I** — `IBackendApiClient` and `ISignalRClient` are defined in `Services/` so `VehicleWorker` depends only on the operations it needs.
- **D** — `VehicleWorker` depends on `IBackendApiClient` and `ISignalRClient`, not on concrete implementations. Register concretes in `Program.cs`.
