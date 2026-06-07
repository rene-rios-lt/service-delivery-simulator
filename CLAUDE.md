# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is the simulator repository for the Service Delivery system. It is a **.NET 10 Worker Service** that drives the POC with realistic vehicle data — it is not production code and does not use Clean Architecture. A single well-organized project with clear internal separation is the right pattern here.

## System Context

The simulator is an **external actor** that calls the backend API the same way a real Telematics integration would. The backend does not know or care whether it is receiving data from the simulator or real hardware. When real Telematics is available, replacing the simulator is a configuration change — not a code change.

The simulator:
- Authenticates with a pre-seeded service account JWT (same auth as all other users)
- Drives 8 vehicles along pre-determined route loops across Iowa (statewide)
- Posts vehicle position updates every **3 seconds** to the backend
- Connects to the backend's SignalR `RepHub` to receive job offers
- Auto-accepts ~85% of job offers, auto-declines ~15% (configurable)
- When a vehicle is assigned a job, it deviates from its loop and navigates toward the requester's location

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

# Run the simulator (requires backend running and appsettings.Local.json configured)
dotnet run --project src/ServiceDelivery.Simulator

# Run tests
dotnet test ServiceDelivery.Simulator.slnx
```

## Local Configuration

Create `src/ServiceDelivery.Simulator/appsettings.Local.json` (gitignored) with your local settings:

```json
{
  "Simulator": {
    "BackendBaseUrl": "https://localhost:5001",
    "SimulatorPassword": "<simulator account password from backend seed data>"
  }
}
```

## Key Behaviors

- **Position updates**: Every 3 seconds, each VehicleWorker advances along its Iowa route waypoints and calls `POST /vehicles/{id}/position`
- **Route loops**: Vehicles traverse ordered waypoint arrays continuously — when the last waypoint is reached, wrap back to the first
- **Job deviation**: When a job is accepted, the vehicle navigates straight-line toward the requester's lat/lng, then returns to the nearest loop waypoint on completion
- **Auto-accept/decline**: Random decision per offer using `AutoDeclineRatePercent` — add a 1–5 second delay before responding to simulate a real rep reviewing the offer
- **Startup sequence**: Authenticate → connect SignalR → start all 8 VehicleWorkers

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
