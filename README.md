# Service Delivery Simulator

A .NET 10 Worker Service that drives the Service Delivery POC with realistic vehicle data. It simulates 8 service vehicles traveling across Iowa, posting position updates to the backend API every 3 seconds and auto-responding to job offers.

## Prerequisites

- .NET 10 SDK
- Service Delivery backend running locally (see backend repo)

## Setup

1. Create `src/ServiceDelivery.Simulator/appsettings.Local.json`:

```json
{
  "Simulator": {
    "BackendBaseUrl": "https://localhost:5001",
    "SimulatorPassword": "<simulator account password>"
  }
}
```

2. Build:

```bash
dotnet build ServiceDelivery.Simulator.slnx
```

3. Run:

```bash
dotnet run --project src/ServiceDelivery.Simulator
```

## How It Works

- 8 `VehicleWorker` background services run concurrently, one per vehicle
- Each worker advances its vehicle along a pre-determined Iowa route loop every 3 seconds
- Position updates are posted to the backend via `POST /vehicles/{id}/position`
- Job offers arrive over SignalR (`RepHub`) and are auto-accepted (~85%) or declined (~15%)
- When a vehicle accepts a job, it navigates toward the requester's location and returns to its loop on completion

See [`docs/simulator-spec.md`](docs/simulator-spec.md) for the full specification.

## Implementing Stories

Stories are implemented using the Master agent in the central repo. Invoke it with a simulator story ID:

```
/master SIM-001
```

The agent runs the full TDD pipeline (evaluate → plan → implement → review → PR) with two human checkpoints. See [service-delivery-central](https://github.com/rene-rios-lt/service-delivery-central) for the full agent system documentation.

## Related Repos

| Repo | Purpose |
|------|---------|
| [service-delivery-central](https://github.com/rene-rios-lt/service-delivery-central) | Architecture docs, ADRs, orchestration |
| [service-delivery-backend](https://github.com/rene-rios-lt/service-delivery-backend) | .NET 10 Clean Architecture API |
| [service-delivery-frontend](https://github.com/rene-rios-lt/service-delivery-frontend) | .NET MAUI Blazor Hybrid frontend |
