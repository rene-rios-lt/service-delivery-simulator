# Simulator Specification

## Role

The simulator is an **external actor** — a separate service that calls the backend API exactly as a real Telematics integration would. The backend has no knowledge of whether it is talking to the simulator or real hardware. When real Telematics is available, replacing the simulator is a configuration change (swap credentials, point to real data source), not a code change.

## Authentication

The simulator holds **two kinds of credentials**, used for two distinct purposes:

1. **The seeded rep accounts (`rep1` … `rep8`, role `ServiceRep`)** — used to make job _decisions_ on behalf of the reps the simulator operates. Each rep the simulator drives gets its own authenticated session and its own `RepHub` connection, so the simulator responds to offers exactly as that rep would. Rep credentials are derived by convention: `rep1@dealer.com` … `rep8@dealer.com`, all sharing a single seed password (`RepPassword`, set in `appsettings.Local.json`).
2. **A single `Simulator`-role account** — used **only** to post vehicle positions for all 8 vehicles and to read fleet/job-state. It never makes job decisions. Credentials (`SimulatorEmail` / `SimulatorPassword`) are stored in `appsettings.Local.json` (gitignored).

On startup, the simulator calls `POST /auth/login` for the `Simulator` account and for each rep account it intends to operate, storing each JWT. Each JWT is included as a Bearer token on that session's requests. If a token expires, the simulator re-authenticates that session automatically.

## Vehicles

8 vehicles are simulated, one per seeded rep (`rep1` … `rep8`). Each vehicle:
- Follows a pre-determined loop route across Iowa (statewide geography) while its rep is idle
- Is driven by the simulator's **Position Engine** (see below), which advances every vehicle's position from backend job-state
- Has its position posted to the backend every 3 seconds **as the `Simulator` account**

Position for all 8 vehicles is **simulator-pushed, not backend-derived** — the backend does not move the trucks; the simulator computes every position and posts it.

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
- Endpoint: `POST /vehicles/{vehicleId}/position`, authenticated as the **`Simulator` account** (positions for all 8 vehicles are posted on this one connection)
- Payload: `{ vehicleId, latitude, longitude }`
- The backend owns all business logic on receipt (15-mile threshold check, ETA recalculation, SignalR broadcast)

### Position Engine

The simulator drives **every** vehicle's position from backend job-state. Position is **simulator-pushed, not backend-derived** — this is deliberate; the backend never moves the trucks. On each 3-second tick the Position Engine decides where each vehicle is and posts it:

- **Idle rep** — the truck loops continuously along its assigned Iowa route (ordered waypoints, wrapping from last back to first).
- **Job accepted** — once a rep's job is accepted (by the simulator's Auto-Decision Engine **or** by a human who has taken the rep over), the truck deviates from its loop and navigates straight-line toward the requester's lat/lng, interpolating along the line on each tick.
- **Job complete** — when the job ends, the truck navigates back to the nearest loop waypoint and resumes normal loop traversal.

The Position Engine drives _all_ trucks this way regardless of who controls the rep — including reps that a human has taken over (see Reconciliation and Yield).

## Job Offers

### Auto-Decision Engine

For the reps it still operates (i.e. **not** human-controlled), the simulator auto-responds to job offers on **each rep's own `RepHub` connection** — there is no longer a single service-account inbox; each operated rep receives and answers its own offers.

On receiving a `JobOfferReceived` event for an operated rep, after a short random delay (1–5 seconds) to simulate a real rep reviewing the offer:
- **~85% of the time**: call `POST /job-offers/{offerId}/accept`
- **~15% of the time**: call `POST /job-offers/{offerId}/decline`

The auto-decline rate is configurable via `appsettings.json` (`AutoDeclineRatePercent`).

On its **own accepted jobs** (jobs the Auto-Decision Engine accepted), the simulator also auto-progresses the job lifecycle once the truck reaches the requester:
1. On arrival at the requester's location (within ~0.1 miles), it calls `POST /rep/arrive` — simulating the rep tapping "I've Arrived".
2. It then **works the job for a randomized 120–240 seconds** — long enough that viewers watching the map see the mechanic at work on site.
3. It calls `POST /rep/complete` — simulating the rep tapping "Mark Complete".

The 120–240 second work window is a code constant, not a configurable value.

## Job Routing (Active Assignment)

Job routing is split between the Position Engine (movement) and the Auto-Decision Engine (lifecycle), with a reconciliation step that decides which reps the simulator operates.

### Reconciliation and Yield

On each tick the simulator reads backend fleet/job-state via a `Simulator`-role read endpoint to learn which reps are **`human-controlled`**:

- It **operates only the non-human reps** — making their job decisions and auto-progressing their accepted jobs — and rebalances those operated reps onto free vehicles.
- **Human takeover happens on the backend**: a take-over endpoint sets the `human-controlled` marker on a rep. The simulator does not initiate takeover; it only observes the marker via the fleet-state read.
- When a human takes over a rep, the simulator **relinquishes that rep and never re-assumes it for the rest of the run** — the assignment is sticky ("gone home for the night"). The simulator stops making decisions and stops auto-progressing that rep's jobs.
- The simulator **still drives that human truck's position**: after the human Accepts a job, the Position Engine navigates the truck toward the requester, then **HOLDS at the requester** until the human taps Arrived/Complete. The simulator does **not** auto-arrive or auto-complete a human's truck — those lifecycle calls belong to the human.

## DTC Injection

Not applicable for the POC. Requesters submit service requests manually through the frontend. The simulator does not generate service requests.

## Configuration

There is **no "number of reps" knob** — the simulator operates the 8 seeded reps/vehicles as-is. Settings are operational only and live in `appsettings.json` / `appsettings.Local.json`:

```json
{
  "Simulator": {
    "BackendBaseUrl": "http://localhost:5180",
    "PositionUpdateIntervalSeconds": 3,
    "AutoDeclineRatePercent": 15,
    "SimulatorEmail": "<set in appsettings.Local.json>",
    "SimulatorPassword": "<set in appsettings.Local.json>",
    "RepPassword": "<set in appsettings.Local.json>"
  }
}
```

- `SimulatorEmail` / `SimulatorPassword` — the single `Simulator`-role account used to post positions and read fleet/job-state.
- `RepPassword` — the shared seed password for the `rep1@dealer.com` … `rep8@dealer.com` accounts; rep emails are derived by convention and need no config.

The 120–240 second on-site work window is a **code constant, not a config value**.

`appsettings.Local.json` is gitignored and must be created locally with the Simulator account password and the shared rep password.

## Related Backend Endpoints

| Action | Endpoint | Authenticated as |
|--------|----------|------------------|
| Authenticate | `POST /auth/login` | each account (Simulator + operated reps) |
| Push position update | `POST /vehicles/{id}/position` | `Simulator` account |
| Read fleet / job-state (which reps are `human-controlled`) | `Simulator`-role read endpoint | `Simulator` account |
| Accept job offer | `POST /job-offers/{id}/accept` | the operated rep |
| Decline job offer | `POST /job-offers/{id}/decline` | the operated rep |
| Mark arrived at destination | `POST /rep/arrive` | the operated rep (own jobs only) |
| Mark job complete | `POST /rep/complete` | the operated rep (own jobs only) |
| Receive job offers | SignalR `RepHub` → event `JobOfferReceived` | one connection per operated rep |

Human takeover is performed via a backend take-over endpoint by the frontend/human — the simulator does not call it; it only observes the resulting `human-controlled` marker through the fleet/job-state read.
