# Simulator Specification

## Role

The simulator is an **external actor** — a separate service that calls the backend API exactly as a real Telematics integration would. The backend has no knowledge of whether it is talking to the simulator or real hardware. When real Telematics is available, replacing the simulator is a configuration change (swap credentials, point to real data source), not a code change.

## Authentication

The simulator authenticates using a pre-seeded service account in the backend database:
- Email: `simulator@system.internal`
- Role: `Simulator`
- Credentials stored in `appsettings.Local.json` (gitignored)

On startup, the simulator calls `POST /auth/login` and stores the JWT. The JWT is included as a Bearer token on all subsequent requests. If the token expires, the simulator re-authenticates automatically.

## Vehicles

8 vehicles are simulated. Each vehicle:
- Follows a pre-determined loop route across Iowa (statewide geography)
- Is represented as a `VehicleWorker` background service
- Advances along route waypoints every 3 seconds
- Posts its current position to the backend every 3 seconds

### Iowa Route Loops

Each vehicle is assigned a loop through a different region of Iowa, covering the state statewide. Loops are defined as ordered arrays of lat/lng waypoints that vehicles traverse continuously. When a vehicle reaches the last waypoint, it loops back to the first.

Approximate territories (to be refined with exact waypoints during implementation):
| Vehicle | Territory |
|---------|-----------|
| V-001 | Des Moines metro |
| V-002 | Cedar Rapids / Iowa City corridor |
| V-003 | Sioux City / northwest Iowa |
| V-004 | Davenport / Quad Cities |
| V-005 | Waterloo / Cedar Falls |
| V-006 | Dubuque / northeast Iowa |
| V-007 | Council Bluffs / southwest Iowa |
| V-008 | Mason City / north central Iowa |

## Position Updates

- Frequency: every **3 seconds**
- Endpoint: `POST /vehicles/{vehicleId}/position`
- Payload: `{ vehicleId, latitude, longitude }`
- The backend owns all business logic on receipt (15-mile threshold check, ETA recalculation, SignalR broadcast)

## Job Offers

The simulator connects to the backend's `RepHub` SignalR hub to receive job offers for the simulator service account.

On receiving a `JobOfferReceived` event:
- **~85% of the time**: call `POST /job-offers/{offerId}/accept` after a short random delay (1–5 seconds) to simulate a real rep reviewing the offer
- **~15% of the time**: call `POST /job-offers/{offerId}/decline` after a short delay

The auto-decline rate is configurable via `appsettings.json` (`AutoDeclineRatePercent`).

## Job Routing (Active Assignment)

When a vehicle accepts a job offer, it deviates from its loop route:
1. The vehicle's next waypoint target becomes the requester's lat/lng (from the job offer payload)
2. The vehicle moves toward the requester on each 3-second tick (interpolating along a straight line)
3. When the vehicle reaches the requester's location (within ~0.1 miles), it stops moving
4. On job completion (backend notifies via SignalR or polling), the vehicle returns to its nearest loop waypoint and resumes the loop

## DTC Injection

Not applicable for the POC. Requesters submit service requests manually through the frontend. The simulator does not generate service requests.

## Configuration

All simulator settings are in `appsettings.json` / `appsettings.Local.json`:

```json
{
  "Simulator": {
    "BackendBaseUrl": "https://localhost:5001",
    "PositionUpdateIntervalSeconds": 3,
    "AutoDeclineRatePercent": 15,
    "SimulatorEmail": "simulator@system.internal",
    "SimulatorPassword": "<set in appsettings.Local.json>"
  }
}
```

`appsettings.Local.json` is gitignored and must be created locally with the simulator's password.

## Related Backend Endpoints

| Action | Endpoint |
|--------|----------|
| Authenticate | `POST /auth/login` |
| Push position update | `POST /vehicles/{id}/position` |
| Accept job offer | `POST /job-offers/{id}/accept` |
| Decline job offer | `POST /job-offers/{id}/decline` |
| Receive job offers | SignalR `RepHub` → event `JobOfferReceived` |
