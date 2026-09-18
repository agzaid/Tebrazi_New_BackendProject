# Offline-first clinic agent — design

**Status: proposal. Nothing described here is built.** Written 2026-09-11, against a port at
119 of 339 endpoints (`PORT-STATUS.md` is the scoreboard).

---

## Purpose

Keep a clinic working when its internet connection drops. Reception keeps registering patients,
doctors keep recording visits, and everything queued reaches the central database when the link
returns — with a clear account of anything that could not be applied.

This document assesses the **local-agent** approach: a small .NET Windows Service on one machine
in the clinic, holding a local SQL Server and a sync outbox, that the browser falls back to when
the cloud API is unreachable.

---

## Recommendation

**Build it, and build the agent out of the existing solution rather than as a new application.**
The approach is sound and the codebase suits it unusually well. But two things in the design as
drawn will not work, and one large piece is missing:

1. **The browser cannot call `http://192.168.1.10:5050`.** The client is served over HTTPS and
   browsers block mixed content. This needs a real certificate on the agent — §3.
2. **The agent cannot be given the JWT secret as things stand.** Signing is HMAC-SHA256, so the
   validating key *is* the signing key. A clinic PC holding it can mint a token for any user in
   the platform — §4.
3. **The design covers writes only.** An outbox does not let reception *find* a patient while
   offline. That needs a pull replica, and it is the larger half of the work — §5.

Sequence: fix the transport and the identity problem first (§3, §4). They are prerequisites, and
each is useful on its own.

---

## 1 · What the codebase already gives you

These are measured, not assumed.

| Asset | Why it matters | Evidence |
|---|---|---|
| **GUID primary keys everywhere** | A client or agent can mint an id offline; replay becomes an idempotent upsert on the PK. The duplicate-patient problem is largely pre-solved. | `@default(uuid())` on 69 Prisma models; `Id = Guid.NewGuid().ToString()` in every .NET entity factory |
| **Eight DbContexts with EF migrations** | The local database is the *same schema*. Point the agent's connection string at a local instance and run the same migrations — no second data model. | `Modules/*/Tebrazi.*.Persistence/Migrations/`, 8 contexts |
| **Mediator + handler per endpoint** | The agent can execute the *same handlers* locally. No parallel business logic. | `Kernel/Tebrazi.SharedKernel/Mediation/`, 124 handlers |
| **Modular monolith, no cross-module DbContext injection** | The agent references the same module assemblies and hosts a subset. Module boundaries already hold. | `Modules/`, cross-module ports in `Kernel/Tebrazi.SharedKernel/Abstractions/` |
| **Stateless JWT auth** | No server-side session to replicate. | `Program.cs:100-140` |

**The consequence worth stating plainly: the agent is not a new application.** It is
`Tebrazi.Api` with a different connection string, a subset of modules, and one new hosted service.
A separate clinic app would duplicate 124 handlers and then drift from them.

---

## 2 · Architecture

```
  CLINIC LAN                                          CLOUD
  ┌───────────────────────────────┐
  │  Reception PC ─┐              │
  │  Doctor PC ────┼── browser    │          ┌────────────────────┐
  │  Admin PC ─────┘     │        │          │  Tebrazi.Api       │
  │                      │        │          │  (Railway)         │
  │            1. try cloud ──────┼──────────▶  └────────┬─────────┘
  │                      │        │          │           ▼
  │            2. on failure      │          │     Cloud SQL Server
  │                      ▼        │          │           ▲
  │        ┌─────────────────────┐│          │           │
  │        │  Tebrazi.Agent      ││          │           │
  │        │  (Windows Service)  ││          │           │
  │        │                     ││          │           │
  │        │  same handlers      ││          │           │
  │        │  local SQL Server   ││  sync    │           │
  │        │  SyncOutbox ────────┼┼──────────┼───────────┘
  │        │  PullReplica        ││          │
  │        └─────────────────────┘│          │
  └───────────────────────────────┘
```

**Fallback belongs in the client, not in a detector.** Never branch on `navigator.onLine` — it
reports the network adapter, not whether the API answers. The rule is: try the cloud, and on a
transport failure or timeout, retry against the agent. A 4xx from the cloud is an *answer* and
must not trigger fallback; only a failure to get an answer does.

---

## 3 · ⚠ The mixed-content blocker

**This stops the design as drawn.** The client is served over HTTPS — `netlify.toml` publishes
`client/dist` and proxies `/api/*` to `https://tebrazi-production.up.railway.app`. A page on an
`https://` origin **cannot** issue a request to `http://192.168.1.10:5050`; the browser blocks it
before it reaches the network, and no CORS header on the agent changes that.

Three ways out, with the trade-off that decides it:

| Option | How | Cost |
|---|---|---|
| **A. Loopback only** | Browsers treat `http://localhost` and `http://127.0.0.1` as secure contexts, so an HTTPS page *may* call them | The agent must run on **every** clinic PC, not one. Loses the shared local database, which was the point |
| **B. Real certificate (recommended)** | A public DNS `A` record — say `agent.clinic.tebrazi.com` — resolving to the clinic's LAN IP, with a Let's Encrypt certificate issued by DNS-01 challenge | One DNS record per clinic; certificate renewal must work without inbound internet. This is what Plex and similar products do |
| **C. Serve the PWA from the agent** | When the cloud is unreachable, the browser loads the app itself from the agent, so origin and API are both local | Two copies of the frontend to keep in version-step; the switch is a full page load, not a graceful degradation |

**Recommend B.** A is not viable for a shared clinic database. C makes every frontend release a
clinic deployment.

Note the interaction with the service worker: `client/public/sw.js` is production-only as of
2026-09-11 and its network-first branch would cache agent responses under the cloud origin's
cache. Any agent traffic must be excluded from the service worker explicitly.

---

## 4 · ⚠ The identity problem

`JwtTokenGenerator.cs:31-32` signs with `SymmetricSecurityKey` + `HmacSha256`, and `Program.cs:117`
validates with the same `SymmetricSecurityKey`. **With HMAC the validating key is the signing key.**

So an agent that validates tokens offline must hold `Jwt:Secret`. Anyone who extracts it from a
clinic PC — a stolen machine, a backup, a support technician — can mint a valid token for **any
user in the entire platform, at every clinic**. That is not an acceptable blast radius for a box
sitting under a reception desk.

**Fix: move token signing to RS256.** The cloud holds the private key and signs; the agent holds
only the public key and validates. A leaked public key is worthless.

Two consequences to plan for:

- **JWTs are currently interchangeable between the Node and .NET backends** — that is a stated
  property of the port (`PROJECT_INFO.md` §1). Changing the algorithm breaks that unless Node
  changes with it. Do this while both backends are still in step.
- Every issued token becomes invalid at the cutover. Plan it as a forced re-login.

**Until RS256 lands, the agent cannot authenticate anyone offline.** The honest interim is a
separate local credential — a clinic-scoped PIN or device certificate that authorises the agent's
own API, distinct from platform identity. `Clinics` already has a staff-PIN concept
(`StaffPinCommands.cs`) that is worth reading before inventing another.

---

## 5 · The missing half: reads

The outbox in the sketch handles writes. But the first thing reception does is **search for a
patient**, and an empty local database cannot answer that. Offline capability is mostly a *read*
problem.

What the agent must hold, continuously pulled while online:

| Data | Scope | Why |
|---|---|---|
| Patients / clinic-patients | this clinic | Search, registration, duplicate avoidance |
| Appointments | this clinic, rolling window (say −7 / +30 days) | The day's schedule |
| Visits | this clinic, recent | Continuing a consultation |
| Prescriptions | for those visits | History at the point of prescribing |
| Users, physicians, clinics | referenced rows only | Names on every screen |

**Never replicate the whole database**, and never replicate another clinic's patients. Pull is
incremental, driven by a per-table high-water mark.

**⚠ The high-water mark needs a column the port does not reliably have.** `UpdatedAt` is now
stamped on insert as well as update (`BaseDbContext.ApplyAuditing`, fixed 2026-09-09), which makes
`WHERE UpdatedAt > @since` viable — but a **hard delete** leaves nothing to pull, and
`DELETE /api/appointments/{id}` is a hard delete by contract (`PORT-STATUS.md`). A hard-deleted row
will linger in the local replica forever. Either add a tombstone table on the cloud side, or accept
that hard-deleted rows need a periodic full reconcile of the affected table.

---

## 6 · The outbox

One row per *business operation*, never per table-row diff.

| Column | Purpose |
|---|---|
| `OperationId` | GUID, minted locally. The idempotency key the cloud dedupes on |
| `EntityType` / `EntityId` | What it concerns. `EntityId` is the GUID PK already minted locally |
| `Operation` | `Create` / `Update` / `Delete` |
| `Payload` | The request body as JSON, exactly as the cloud endpoint expects |
| `DependsOn` | `OperationId` of an operation that must succeed first — §6.1 |
| `Status` | See §6.2 |
| `AttemptCount`, `NextAttemptAt`, `LastError` | Retry state and the message a human reads |

**The write and its outbox row commit in one local transaction.** If they can diverge, the queue
lies.

### 6.1 Ordering and dependencies

Create a patient offline, then book them an appointment. If the patient create fails validation in
the cloud, the appointment **must not** be sent — it would reference a patient that does not exist.

Strict global FIFO is the simple answer and is usually wrong: one poisoned operation blocks the
whole clinic's queue behind it. Use `DependsOn`:

- An operation whose dependency is `Pending` waits.
- An operation whose dependency is `Failed` becomes `Blocked` — it is not retried and not lost.
- Independent chains keep draining.

Because ids are GUIDs minted locally, the child already knows its parent's id before the parent has
ever reached the cloud. That is what makes this tractable.

### 6.2 Error taxonomy — what the clinic actually sees

This is the part you asked for: *"if there's something wrong, give an error message of what
happened."* Three outcomes, three behaviours:

| Cloud response | Status | Behaviour | Shown to |
|---|---|---|---|
| Transport failure, timeout, `5xx`, `429` | `Pending` | Retry with exponential backoff and jitter. Not a user's problem yet | Nobody, until it is old |
| `400` / `403` / `404` — validation or permission | `Failed` | Stop. Never retry; the same body will fail identically forever | **Clinic admin**, with the endpoint, the entity, and the cloud's own `error` string |
| `409` — conflict | `Conflict` | Stop. Someone changed the same record centrally | **Clinic admin**, showing both versions |

The middle row is where a naive implementation loses data silently: a `400` retried forever looks
like a transient failure on a dashboard and is never surfaced. **Failure must be loud and
attributable** — which operation, which patient, which endpoint, what the server said.

A `Failed` row is not deleted. It stays until a human resolves or discards it.

### 6.3 Cloud-side idempotency

`OperationId` must be recorded centrally, and a replay of one already applied must return the
**original** response rather than creating a second row. Without this, a reply lost on the wire
becomes a duplicate patient on the next retry.

This is an additive change to the cloud API — a header and a dedupe table. It does not alter any
existing response body, so the fixed JSON contract survives. Say so explicitly in
`PORT-STATUS.md` when it lands.

---

## 7 · Requirements

**R-01.** The agent shall accept the same request bodies as the cloud API for the endpoints it
supports, and return the same response shapes, so the client needs one code path per endpoint.

**R-02.** A local write and its outbox row shall commit in a single transaction against the local
database.

**R-03.** The client shall fall back to the agent only on transport failure or timeout of a cloud
request — never on a `4xx`, and never on `navigator.onLine` alone.

**R-04.** An operation replayed to the cloud with an `OperationId` already applied shall return the
original response and create no second row.

**R-05.** An operation whose `DependsOn` target is `Failed` shall be marked `Blocked` and never
sent.

**R-06.** A `Failed` or `Conflict` operation shall be visible to a clinic administrator with its
entity type, entity id, target endpoint, attempt count, and the verbatim `error` string the cloud
returned.

**R-07.** The agent shall serve its API over TLS on a hostname the browser accepts from an HTTPS
origin (§3).

**R-08.** The agent shall validate platform JWTs using a **public** key only (§4), or shall use a
local credential scheme that is not platform identity.

**R-09.** Pull replication shall be scoped to the clinic the agent serves, and shall never hold
another clinic's patient data.

---

## 8 · Validation

There is no test project anywhere in the solution — 38 `.csproj`, none of them a test project.
These are the checks that will actually be run:

**V-01.** `dotnet build Tebrazi.Backend.sln --no-incremental -v q --nologo` — warning count compared
against the 216 baseline.

**V-02.** Agent and cloud both running: the same request sent to each, response bodies diffed.
This is the only claim that settles an agent-parity question.

**V-03.** Cable-pull test. Disconnect the clinic's WAN mid-session; register a patient, book an
appointment for that patient, record a visit; reconnect; confirm all three appear centrally, in
order, exactly once.

**V-04.** Poison-message test. Queue an operation the cloud will reject with a `400` behind one it
will accept. Confirm the accepted one syncs, the rejected one reaches `Failed` with the cloud's
error string, and its dependent reaches `Blocked` rather than being sent.

**V-05.** Duplicate-replay test. Replay a batch whose responses were lost after the cloud committed
them. Confirm no duplicate rows.

---

## 9 · Risks, and what is not covered

**Known and material:**

- **The identity change (§4) is a platform-wide change**, not an agent feature. It invalidates every
  issued token and must be coordinated with the Node backend while JWTs are still interchangeable.
- **Hard deletes break incremental pull** (§5). Needs tombstones or a periodic reconcile.
- **Clock skew.** A clinic PC with a wrong clock writes wrong `createdAt` values that then sync
  centrally. Stamp authoritative timestamps on arrival at the cloud, keep the local time as a
  separate `RecordedAt`.
- **Agent-machine failure is now a clinic outage** and its local database holds the only copy of
  unsynced work. It needs a backup story before it holds anything.
- **Schema drift.** Eight contexts and their migrations must be applied to every clinic's local
  database. An agent on an older schema will produce payloads the cloud rejects — version the agent
  and refuse to sync across a mismatch rather than failing per-operation.
- **`DateTime` wire format** was `PORT-STATUS.md` decision #1 and is **fixed as of 2026-09-18** —
  `NodeDateTimeJsonConverter` pins every timestamp to Node's `.fffZ`. Sync payloads inherit the
  fix, provided they go through the host's MVC serializer options rather than their own.
- **SQL Server NULL-distinctness** is already recorded (`PORT-STATUS.md` #4) and bites harder here —
  a unique index that behaves differently locally and centrally makes an operation succeed on one
  side and fail on the other.

**Assumed, not verified:**

- That clinics have a machine that stays on, and someone who can install a service on it.
- That a public DNS record per clinic pointing at a private IP is acceptable operationally.
- Nothing here has been prototyped. No measurement in this document comes from a running agent.

**Deliberately not covered:**

- **Which endpoints the agent supports.** Not all 119 need to work offline. Registration, search,
  appointments and visits are the plausible core; analytics, manager reporting and AI endpoints are
  not. That subset is the next decision and it should be made from clinic workflow, not from the
  code.
- Bidirectional conflict *merge*. §6.2 stops at detection and hands a conflict to a human. Automatic
  merge is a much larger problem and is not proposed.
- File and image sync (`Documents` module).
- Multi-agent clinics, or two agents on one LAN.
