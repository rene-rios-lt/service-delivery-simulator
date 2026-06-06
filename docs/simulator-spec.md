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

Each vehicle covers a distinct Iowa territory. Waypoints are ordered — vehicles traverse them continuously, wrapping from the last back to the first.

**V-001 — Des Moines metro**
```
41.6002, -93.7224  // Clive
41.5700, -93.7111  // West Des Moines
41.6266, -93.7120  // Urbandale
41.6733, -93.7073  // Johnston
41.7308, -93.6064  // Ankeny
41.6461, -93.4710  // Altoona
```

**V-002 — Cedar Rapids / Iowa City corridor**
```
42.0082, -91.6440  // Cedar Rapids
42.0469, -91.6816  // Hiawatha
42.0341, -91.5973  // Marion
41.7497, -91.6082  // North Liberty
41.6874, -91.5824  // Coralville
41.6611, -91.5302  // Iowa City
```

**V-003 — Sioux City / northwest Iowa**
```
42.4999, -96.4003  // Sioux City
42.7952, -96.1659  // Le Mars
42.6469, -95.2086  // Storm Lake
43.1419, -95.1442  // Spencer
43.0769, -95.6260  // Estherville
42.8360, -96.0126  // Cherokee
```

**V-004 — Davenport / Quad Cities**
```
41.5236, -90.5776  // Davenport
41.5245, -90.4410  // Bettendorf
41.5978, -90.3442  // Le Claire
41.8294, -90.5378  // DeWitt
41.8444, -90.1887  // Clinton
41.4244, -91.0435  // Muscatine
```

**V-005 — Waterloo / Cedar Falls**
```
42.4928, -92.3426  // Waterloo
42.5244, -92.4531  // Cedar Falls
42.4057, -92.4624  // Hudson
42.4721, -92.2782  // Evansdale
42.4744, -92.0635  // Jesup
42.4652, -91.8897  // Independence
```

**V-006 — Dubuque / northeast Iowa**
```
42.5006, -90.6646  // Dubuque
42.4816, -91.1289  // Dyersville
42.4838, -91.4560  // Manchester
42.8539, -91.4054  // Elkader
43.2688, -91.4741  // Waukon
43.3069, -91.7882  // Decorah
```

**V-007 — Council Bluffs / southwest Iowa**
```
41.2619, -95.8608  // Council Bluffs
41.6527, -95.3272  // Harlan
41.4766, -95.3366  // Avoca
41.4033, -95.0139  // Atlantic
41.0013, -95.2302  // Red Oak
40.7651, -95.3697  // Shenandoah
```

**V-008 — Mason City / north central Iowa**
```
43.1536, -93.2010  // Mason City
43.1380, -93.3802  // Clear Lake
43.2630, -93.6378  // Forest City
43.1004, -93.6017  // Garner
42.7405, -93.2010  // Hampton
42.5218, -93.2601  // Iowa Falls
```

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
3. When the vehicle reaches the requester's location (within ~0.1 miles), it calls `POST /rep/arrive` — simulating the rep tapping "I've Arrived"
4. After a configurable on-site delay (default: `OnSiteDelaySeconds = 30`), it calls `POST /rep/complete` — simulating the rep tapping "Mark Complete"
5. The vehicle then navigates back to its nearest loop waypoint and resumes normal loop traversal

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
    "OnSiteDelaySeconds": 30,
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
| Mark arrived at destination | `POST /rep/arrive` |
| Mark job complete | `POST /rep/complete` |
| Receive job offers | SignalR `RepHub` → event `JobOfferReceived` |
