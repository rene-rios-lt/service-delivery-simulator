# Service Delivery Simulator

A .NET 10 Worker Service that drives the Service Delivery POC with realistic vehicle data. It operates the seeded rep accounts to make job decisions and pushes every truck's position to the backend API every 3 seconds — while letting a human take over any idle vehicle from a device (see central repo ADR-0009, "Human Takeover").

## Prerequisites

- .NET 10 SDK
- Service Delivery backend running locally (see backend repo)

## Setup

1. Create `src/ServiceDelivery.Simulator/appsettings.Local.json`:

```json
{
  "Simulator": {
    "BackendBaseUrl": "http://localhost:5180",
    "SimulatorPassword": "<Simulator-role account password — posts vehicle positions>",
    "RepPassword": "<shared password for rep1…rep8 — logs in as each rep to make job decisions>"
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

- It logs in as the seeded rep accounts `rep1…rep8` (job decisions) and holds one `Simulator`-role account (vehicle positions only) — there is no single auto-accepting service account
- A position engine drives **every** truck's position from backend job-state and posts it to `POST /vehicles/{id}/position` every 3 seconds
- For each rep it operates, job offers arrive over SignalR (`RepHub`) and are auto-accepted (~85%) or declined (~15%); accepted jobs auto-arrive, work on-site for a randomized 120–240 seconds, then auto-complete
- A human can log in as `rep1…rep8` on a device, pick an idle vehicle, and take it over — the simulator yields that rep for the rest of the run and never re-assumes it (abandoned jobs re-match)
- For a human-operated truck, the position engine still drives it: navigating to the requester after the human Accepts, then holding until the human taps Arrived/Complete

See [`docs/simulator-spec.md`](docs/simulator-spec.md) for the full specification.

## Implementing Stories

Stories are implemented using the Master agent in the central repo. Invoke it with a simulator story ID:

```
/master SIM-001
```

The agent runs the full TDD pipeline (evaluate → plan → implement → AI review → PR) with two human checkpoints. See [service-delivery-central](https://github.com/rene-rios-lt/service-delivery-central) for the full agent system documentation.

## Related Repos

| Repo | Purpose |
|------|---------|
| [service-delivery-central](https://github.com/rene-rios-lt/service-delivery-central) | Architecture docs, ADRs, orchestration |
| [service-delivery-backend](https://github.com/rene-rios-lt/service-delivery-backend) | .NET 10 Clean Architecture API |
| [service-delivery-frontend](https://github.com/rene-rios-lt/service-delivery-frontend) | .NET MAUI Blazor Hybrid frontend |
