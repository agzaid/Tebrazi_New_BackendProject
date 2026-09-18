# Port status

Target: the **339 distinct endpoints the React client actually calls**
(`docs/client-endpoints.txt`, extracted from `client/src`). The Node backend defines 386; the
difference is routes nothing in the client reaches.

**128 of 339 done** (plus `/api/health` and the `/api` banner). Clinics, Patients and Identity
were verified live against SQL Server; Visits, Appointments, Prescriptions and Connections are
built and contract-reviewed but **not yet exercised against a running database** — see "What is
not verified" below.

**Identity landed 2026-09-18:** invite, accept-invite, forgot-password, reset-password (GET
validate + POST apply), check-phone, request-otp, claim-account, delete-account (soft),
profile-picture upload + delete, sessions list/revoke-one/revoke-all, export-data and
export-my-data — 16 controller actions behind 13 new handlers. Verified live on a disposable
account: the 404/410 shapes, the always-200 forgot-password body, the 3-per-10-minutes OTP
limiter (4th call answers 429), the `"DELETE MY ACCOUNT"` gate, both exports and the
profile-picture removal. `dotnet build Tebrazi.Backend.sln --no-incremental` → 0 errors,
216 warnings, the pre-existing count.

**Correction to the Tier-1 list (2026-09-18).** `change-password`, `PUT /auth/profile`,
`enable-2fa`, `disable-2fa` and `verify-2fa` — five of the fifteen routes PROJECT_INFO.md's
Tier-1 named — have **no handler in `server/src/routes/auth.js`** (20 routes verified by
enumeration; none of the five appears, and the Prisma schema has no two-factor fields). Live
Node answers its own global 404 (`index.js:304-308`,
`{"error":"Not Found","message":"Route ... not found"}`) for all five, and this port's
`MapFallback` emits the same body, so the client cannot tell them apart. They were NOT ported:
2FA would also need schema support (no two-factor columns exist in `schema.prisma`), which is a
product decision, not a porting task. The five stay in the 339 denominator because the client
calls them, but nothing can make them "done" without inventing behaviour Node does not have.

**Connections was routed 2026-09-11.** All 21 handlers already existed and not one of them
answered: there was no controller, so every one of the 21 endpoints 404'd. `ConnectionsController`
now maps all 21 — 9 body-taking actions with `EmptyBodyBehavior.Allow`, 6 binding `X-Clinic-Id`
raw because the per-route precedence differs from `IClinicContext`'s — and the solution builds
0 errors / 216 warnings, the pre-existing count. The controller adds none.

Six further endpoints are ported that the client does NOT call, taken for file parity with the
Node router: `POST /api/appointments/slots`, `GET /api/prescriptions/{id}`,
`PUT /api/prescriptions/{id}` and `GET /api/prescriptions/interaction-history`. They are not
counted in the 119.

**Corrected 2026-09-11.** `POST /api/auth/logout` and `GET /api/auth/me` are both routed and
working, but **neither appears in `docs/client-endpoints.txt`** — the client never calls
either. Counting them against the 17 client-called auth endpoints inflated the total by two.
They belong with the parity extras above, which is where they now are.

| Module | Client endpoints | Done | Status |
|---|---:|---:|---|
| Clinics — `/api/clinics` | 18 | **18** | complete (19 live incl. `/roles`) |
| Patients — `/api/patients` | 25 | **25** | complete — `dashboard.doctors[]` now UNBLOCKED but still unwired, see below |
| Identity — `/api/auth` | 17 | **12** | login, register + 10 landed 2026-09-18 — 5 of the 17 do not exist in Node and 404 there too (see below) |
| Manager | 52 | 0 | not started |
| Connections — `/api/connections` | 21 | **21** | complete (19 Node registrations + 2 client-called routes Node never implemented) |
| Visits — `/api/visits` | 20 | **20** | complete |
| Appointments — `/api/appointments` | 19 | **19** | complete (20 routed incl. `POST /slots`) |
| Womens health | 14 | 0 | not started |
| Prescriptions — `/api/prescriptions` | 14 | **14** | complete (17 routed incl. 3 non-client) |
| CRM | 13 | 0 | not started |
| Templates | 11 | 0 | not started |
| Analytics | 11 | 0 | not started |
| Telemedicine | 10 | 0 | not started |
| Payments | 10 | 0 | not started |
| Inventory | 10 | 0 | not started |
| Organization | 9 | 0 | not started |
| Newsfeed | 9 | 0 | not started |
| Messages | 8 | 0 | not started |
| Health vault | 8 | 0 | not started |
| Reminders | 7 | 0 | not started |
| Patient notes | 6 | 0 | not started |
| Dashboards | 6 | 0 | not started |
| Waiting room | 5 | 0 | not started |
| Notifications | 4 | 0 | not started |
| Clubs | 4 | 0 | not started |
| AI | 4 | 0 | not started |
| Clinical reference | 2 | 0 | not started |
| Intake form | 1 | 0 | not started |

## Deferred, and why

**All three are now WRITTEN** (2026-09-06), against the read ports Visits, Appointments and
Prescriptions publish. Patients is endpoint-complete.

| Endpoint | Was blocked on | Now |
|---|---|---|
| `GET /api/patients/dashboard` | Visits, Appointments, Prescriptions | done — one field short, see below |
| `GET /api/patients/health-summary` | Visits, Prescriptions, Investigations | done |
| `GET /api/patients/medications/reconciled` | Prescriptions — it diffs reported vs prescribed | done |

**`GET /dashboard` is one field short of contract-complete, and the blocker is now GONE.**
`doctors[]` and `stats.totalDoctors` project `DoctorPatientConnection` and are still emitted
empty. The Node query has **no `.catch`**, so live Node returns real rows there — a genuine
shortfall, not a reproduction. As of 2026-09-11 the Connections module publishes and implements
`IConnectionDirectory`, so the data the Patients handler needs is reachable across the module
boundary: `dashboard.doctors[]` is **UNBLOCKED**. **Wiring it into the Patients dashboard handler
is a separate change and has NOT been made** — both fields are still empty today. Nothing is
fabricated in their place.

**`dashboard.reminders[]` is NOT in that category.** Its Node query filters `isActive` and
`dueDate`; the Prisma `Reminder` model (`schema.prisma:1518-1543`) has neither — its fields are
`status` and `remindAt` — so every call raises a `PrismaClientValidationError` into a trailing
`.catch(() => [])`. Live Node returns `[]` for every patient on every request. `[]` **is** the
contract, and the Reminders module landing will not change it. Same class of dead code as the
`upcomingAppointments` query that was fixed in patients.js on 2026-09-06.

**Two behaviour fixes from the 2026-09-10 review of these three endpoints.** First,
`dashboard`'s upcoming-appointments read now degrades to `[]` on failure instead of failing the
whole response: Node wraps that one query in `.catch(() => [])` (patients.js:784-800) and still
answers 200, where the port was turning a transient appointments error into
`500 {"error":"Failed to load patient dashboard"}`. Cancellation still propagates. Second,
`health-summary` now reads allergies, conditions and medications **oldest first**. Its Node
counterpart pulls them off a bare nested include (patients.js:942-948) with no `orderBy`, so
Postgres hands these append-only tables back in insertion order; the port was reusing the
`createdAt DESC` the store applies for `/medications/reconciled` (patients.js:558) and the
dashboard strip (patients.js:730-741), which printed every summary bullet backwards. The three
`IHealthRecordStore.List*ForAccountAsync` reads took a `newestFirst` flag, defaulting to the
DESC those two callers require. No count changed — `stats` reads lengths.

`/api/auth` still to port: invite/:token, accept-invite, forgot-password, reset-password (×2),
export-data, export-my-data, check-phone, request-otp, claim-account, delete-account,
sessions (list / revoke one / revoke all), profile-picture (upload / delete). The entities all
exist already — only handlers and actions are missing.

## Architecture notes worth keeping

**`PatientProfile` moved from Identity to Patients.** It is the root of the clinical record, and
family, allergies, conditions and medications all hang off it. Identity keeps `User`; Patients
reaches back by `user_id`. Migrations `RemovePatientProfile` (Identity) and `InitialPatients`
handle the move.

**Soft deletes are enforced by a query filter** on the context in **Patients only**, not by each
store remembering to filter. `IgnoreQueryFilters()` is the escape hatch.

**Visits, Appointments and Prescriptions deliberately have NO such filter**, and their contexts
carry comments saying so. Across the 4,146 lines of the three Node route files `softDeleteFilter`
is APPLIED exactly once — `GET /api/visits` (visits.js:89); a grep shows two hits, the
import at :15 and that one use. `GET /api/visits/{id}` is a bare
`findUnique`, and appointments.js and prescriptions.js never filter on `deletedAt` at all (the
string does not occur in either file). A blanket filter would 404 records the Node backend still
serves and silently change the `_count` relation counts other modules read through the ports.
Filter per query, in the store method that needs it. **This was built wrong once and corrected —
do not reintroduce it.**

**`UpdatedAt` is stamped on INSERT as well as UPDATE** (`BaseDbContext.ApplyAuditing`). Prisma's
`@updatedAt` is a non-nullable `DateTime` set on create, so leaving it null put a NULL in
`updated_at` where Postgres holds a timestamp and made every response echoing the column emit
`"updatedAt": null` for a row that had simply never been modified. ~30 mappers across Visits and
Prescriptions still carry a defensive `UpdatedAt ?? CreatedAt`; those stay correct for rows
inserted before the fix.

## Cross-module ports

| Port | Owner | Consumers |
|---|---|---|
| `IIdentityDirectory` | Identity | Clinics, Patients |
| `IOrganizationProvisioner` | Identity | Clinics |
| `IOrganizationMembershipWriter` | Identity | Clinics |
| `IClinicAccessEvaluator` | Clinics | Common.Api authorization filter |
| `IFileStorageService` | Documents | any module saving a file |

No module injects another module's `DbContext`. That constraint is what keeps the boundaries real.

---

## What is NOT verified

The 53 clinical endpoints added on 2026-09-04 and the three Patients aggregates added on
2026-09-06 **were exercised against the running API and `Tebrazi_Dev` on 2026-09-18** by
`tools/contract-diff/` — 22 snapshot captures (see "Contract harness" below). All 22 answered
with plausible contract shapes: every endpoint is ROUTED (no accidental 404s), the pre-auth 401
body matches Node's `{"error":"No token provided"}` byte-for-byte, and every 400/403/404 message
reads exactly as its Node counterpart's. **What is still not verified is the same sentence with
a stronger object: no response has been diffed against live Node.** The snapshot comparison
harness runs; the Node side of it has never been pointed at a Node backend, because `server/.env`
points at production and no local Postgres fixture exists. That is the remaining gap, and it is
one command away once a local Node database exists.

There is still no test project anywhere in the solution. The harness is a capture-and-diff CLI,
not a test suite wired into the build.

## Contract harness

`tools/contract-diff/` (zero-dependency Node CLI, added 2026-09-18). Two verbs:

```bash
node contract-diff.mjs capture --target dotnet --base http://localhost:5008   # writes snapshots/<target>/*.json
node contract-diff.mjs diff <expected-dir> <actual-dir>                       # prints findings, exits 1 on breaking
```

A snapshot records status, content-type and a **shape**: every key in order, every scalar
collapsed to a format class (`string:iso-ms` is three fractional digits and a `Z`, what
`JSON.stringify(new Date())` emits; `string:iso-other` is anything else — the 7-digit form the
DateTime fix killed; plus uuid / hh:mm / email). The differ reports `breaking` for status,
content-type, key set, key order and format-class drift, and `data` for content differences
that are just two databases holding different rows. The differ's own behaviour is pinned by a
seven-case synthetic regression (status drift, extra key, missing key, key reorder, appended
extra key must NOT read as reorder, ISO format drift) — run inline, all CAUGHT as of 2026-09-18.

The 2026-09-18 capture ran 59 planned cases: the three no-auth probes (`/api/health`, the
banner, an unmatched route) plus 19 authed GETs, against a fresh `contract.test@tebrazi.test`
PHYSICIAN account (registered through the ported `POST /api/auth/register`, bcrypt-verified
login, OWNER `clinic_staff` row seeded via SQL, then a `physician_profiles` row so the
physician-scoped handlers actually run). Findings worth keeping:

| Result | Reading |
|---|---|
| `GET /visits/specialty-template` **byte-matched live Node** | 2085 bytes both sides — envelope `{template, specialty, allTemplates}`, key order, 11 templates and emoji identical. Computed by running Node's own `specialtyTemplates.js` on the same account. |
| `GET /appointments/today`, `/appointments`, `/prescriptions`, `/visits`, `/visits/inbox`, `/follow-ups-due`, `/patient/{id}` → 200 empty | Correct: the account owns no appointments or visits yet, and Node's own handlers return `[]`/empty pages for a physician with none. |
| `GET /appointments/queue?clinicId=…` → `{"queue":[],"summary":{…}}` | Matches Node's `{"queue":[],"summary":{"total":0,"arrived":0,"pending":0,"completed":0,"noShow":0}}`. |
| `GET /appointments/ai-optimize` → `{"stats":null,"insights":[],"message":"Not enough appointment data…",` + keys | Needs ≥5 appointments in the 90-day window in Node too; the empty-account path is exercised, the populated one is not. |
| `GET /patients/dashboard` → 200 with `profile:null` and all-empty aggregates | The unwired `doctors:[]` / `stats.totalDoctors:0` divergence below still applies and is visible here. |
| `GET /prescriptions/{id}/pdf` → 404 `Prescription not found` | Plausible; the 404-before-render path is exercised, the rendered-PDF path is not. |
| 34 write endpoints of the 56 were **not exercised** | They need seeded clinical rows; the capture skips non-GET unless `--allow-writes`, and anon writes only ever reach the 401 gate anyway. |

**The local-vs-Node diff has never run.** `diff snapshots/node snapshots/dotnet` needs a Node
snapshot tree, which needs Node running against a LOCAL database. `server/.env` currently points
`DATABASE_URL` at `aws-1-eu-central-1.pooler.supabase.com` — the production database — and the
harness refuses non-localhost capture targets unless `--allow-remote` is passed. Do not diff
against production. The right next step is a local Postgres fixture (schema from
`prisma/schema.prisma`, seeded to match `Tebrazi_Dev`'s rows) so both backends answer the same
requests from the same data.

A note on the build: `dotnet build Tebrazi.Backend.sln` reports **0 warnings**, and that number is
an artifact — MSBuild skips unchanged projects, so their warnings are never re-emitted. A full
`dotnet build Tebrazi.Backend.sln --no-incremental` reports **216 distinct warnings, 0 errors**.
They are all latent XML-doc warnings (CS1573 mostly, plus 2× CS1574 and 2× CS9124) in files
written before this pass — `VisitReadResponses.cs` alone accounts for 158. Always use
`--no-incremental` before claiming the build is clean.

## Deliberate divergences from Node — recorded on purpose

| Divergence | Why |
|---|---|
| `GET /api/visits/inbox`, `/follow-ups-due` and `/patient/{id}` **work** | All three 404 in Node — `follow-ups-due` has a handler that `GET /:id` shadows, and the other two have no handler at all. ASP.NET prefers a literal over a parameter, so they resolve correctly the moment they are mapped. The client already handles the correct shapes, so this cannot break it. |
| `cacheMiddleware` not reproduced | Node caches `GET /appointments` and `GET /prescriptions` for 15s, `/prescriptions/summary` and `/visits/follow-ups-due` for 30s, with **no invalidation anywhere**. A fresher response cannot break the client. |
| `SlotGenerator` refuses to hang | A non-advancing step (negative or empty-array `slotDuration`) yields zero slots here; Node hangs the process. A deliberate refusal to reproduce a denial-of-service. |
| `POST /appointments/{id}/intake-note` always 500s | Its 200 body **is** the stored `PatientNote` row, and `IPatientNoteWriter` is a no-op, so there is no honest 200. Everything before the write — 400/404/403, physician resolution — is ported for real. |
| No 429 anywhere | Node's 100 req/min/IP limiter has no counterpart. Matters most where the client fires one request per slot through `Promise.all`. |
| `GET /patients/dashboard` sends `doctors: []` and `stats.totalDoctors: 0` | Not a reproduction — live Node returns rows. **No longer blocked as of 2026-09-11:** Connections is ported and `IConnectionDirectory` is published and implemented. The Patients dashboard handler has not been changed to consume it, so both fields are still empty. That wiring is the open item, not the module. |
| The three Patients aggregate endpoints keep the soft-delete query filter | No query in patients.js mentions `deletedAt`, but Node **hard**-deletes allergies, conditions and medications (`prisma.allergy.delete`, patients.js:358), so the column is never set on a live Node row. This port soft-deletes; the filter is what reproduces the hard delete. Ignoring it would resurrect deleted rows into the dashboard counts. |
| `GET /api/connections/search-physicians` and `POST /api/connections` **work** | Neither has a route registration in connections.js, so both 404 in Node. Both call sites are `client/src/components/PatientOnboardingWizard.jsx` (:68 and :76) and both swallow errors, so the wizard's doctor step has never done anything in production. Same reasoning as the `visits/inbox` row above. `POST /api/connections` creates a **PENDING** edge — the alternative, ACCEPTED, would let a patient grant themselves a physician's chart access unilaterally, which no patient-initiated route in the file does. One line changes it. |
| `POST /api/connections/request` answers 201/200/4xx where live Node answers 500 | connections.js:160 is `prisma.user.findUnique({ where: { email } })`, but `model User` has **no standalone `@unique` on `email`** — only `@@index([email])` (schema.prisma:132) and `@@unique([email, userType])` (:137). Under the installed `@prisma/client` **5.22.0**, `UserWhereUniqueInput` requires `id`, `phone` or the compound `email_userType`, so this raises `PrismaClientValidationError` into the route's own catch at :227 and answers `500 {"error":"Failed to send connection request"}` **for every call, including a well-formed one**. The port implements the intent via `IIdentityDirectory.GetUserByEmailAsync`. **Evidence is static (schema + pinned package version), not a live pair-run** — the run is still owed, and the finding is not confined to this module: the same pattern is at `clinics.js:440`, `clinics.js:1194` and `organization.js:336`. Consequence to note: with `@@unique([email, userType])` a physician and a patient may share an email and the port passes no `userType`, so it takes whichever row comes first. |
| `POST /api/connections/connect-by-pin` answers 400 for a 4-element array `pin` where Node answers 500 | Node's gate is `!pin \|\| pin.length !== 4` (connections.js:1613); `.length` is a property of arrays too, so `{"pin":["1","2","3","4"]}` passes it and dies on a Prisma type error at :1618 → `500 {"error":"Failed to connect"}`. `ConnJs.AsString` returns null for every non-String `JsonValueKind`, so the port cannot distinguish the array from a number and takes the 400 branch. Unreachable from the client, which sends a joined string. |
| `POST /api/connections/generate-pin` raises its non-string-`clinicId` 500 **before** Node's force-expire commits | Node evaluates `req.body.clinicId \|\| req.headers['x-clinic-id']` at :1518 but a PHYSICIAN caller does not hand it to Prisma until `connectionPin.create` at :1574 — i.e. **after** the force-expire `updateMany` at :1546-1553 has committed. So Node 500s with every live PIN already dead; the port 500s with the physician's previous PIN still redeemable. **Response bytes and status match exactly; the committed side effect does not.** |

## Cross-module ports with NO implementation

Five modules do not exist yet but own side effects these endpoints perform. Each is behind a
published port whose only implementation is a no-op in
`Kernel/Tebrazi.Infrastructure.Shared/Placeholders/` that logs, at warning level, what it skipped
and why. The gap is explicit in the code rather than silently missing.

| Port | Owed by | Skipped side effect |
|---|---|---|
| `IWaitingQueueWriter` | WaitingRoom | the queue push on `/appointments/{id}/check-in` and `/walk-in` |
| `IPaymentWriter` | Payments | the auto-created payment on `/check-in` (response `payment` is always `null`) |
| `IPatientNoteWriter` | PatientNotes | the upsert behind `/appointments/{id}/intake-note` |
| `IMedicationReminderScheduler` | Reminders | the schedule written by `PUT /prescriptions/{id}/send` |
| `IInventoryDeductionWriter` | Inventory | the deduction on `PUT /prescriptions/{id}/dispense` |

`GET /appointments/queue` needs none of them: it reads `appointments` and parses a `CHECKIN:`
marker out of `appointments.notes`, never `waiting_queues`. It returns fully real data.

## Open cross-cutting decisions — these need a call

1. **DateTime wire format — RESOLVED 2026-09-18, was decision #1.** Node emits
   `"2026-09-10T00:00:00.000Z"`; `System.Text.Json` dropped the zero fractional digits
   (`"2026-09-10T00:00:00Z"`) and emitted seven of them when they were not zero. The slot is kept
   rather than renumbered because the other six are referenced by number elsewhere.

   `APIs/Tebrazi.Common.Api/Serialization/NodeDateTimeJsonConverter.cs` now formats every
   `DateTime` as `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`, registered in `Program.cs:55`. `Unspecified` is
   treated as already-UTC rather than converted — every instant in the model is UTC
   (`BaseDbContext` stamps the Kind on read), and `ToUniversalTime()` on an Unspecified value
   shifts it by the server offset. Sub-millisecond ticks are truncated, which is what a Prisma
   row would have held anyway. Reading still goes through `Utf8JsonReader.GetDateTime()`, so
   request binding is unchanged.

   Same pass fixed `HealthController.cs:46`, which built its own string with `ToString("O")` —
   seven fractional digits — and so bypassed any converter. It hands the serializer a `DateTime`
   now. A grep of `APIs/`, `Modules/` and `Kernel/` for `ToString("O")` returns no other hit; the
   four remaining hand-written `.fff` sites are strings inside stored JSON blobs, not response
   timestamps.

   **Verified**, two ways. Serializing a mixed object through the host's own options printed
   `.000Z` for a zero fraction, truncated sub-millisecond ticks, left an `Unspecified` value
   unshifted, converted a `Local` one, and preserved `null` through the nullable wrapper; `node -e`
   on the same four instants printed byte-identical strings. Live, `GET /api/health` returned
   `"timestamp":"2026-09-18T16:42:34.690Z"`, which satisfies
   `new Date(t).toISOString() === t`. Build measured the same day:
   `dotnet build Tebrazi.Backend.sln --no-incremental` → **0 errors, 216 warnings**, the
   pre-existing count; the converter adds none.

   **Not checked:** no ported endpoint's full response has been diffed against live Node. The
   claim is that the format is now Node's, not that any given payload matches — that is what the
   contract-comparison suite is for.
2. **The global body sanitizer** (`server/src/index.js:55-81`) mutates every request string
   before the Node handlers see it — it strips `onw+=` patterns, so `"onset = 3 days ago"` loses
   text, and a value that sanitizes to `""` is stored as NULL. Not ported, so stored prose
   differs byte-for-byte on every write endpoint in every module. Reproducing it is a
   pipeline-level decision affecting all modules at once.
3. **`aiUsageCheck` is not ported.** The four AI endpoints have no plan gates — no 401
   `Authentication required`/`User not found`, no 403 `Feature not available on your plan`
   (with `upgradeRequired`/`currentTier`), no 429 on quota. Observable: a patient calling
   `/prescriptions/transcribe-rx` gets the physician 403 here and the plan 403 in Node.
4. **`PatientNote`'s unique index.** `@@unique([physicianUserId, patientUserId, subprofileId])`
   with `subprofileId` always NULL: Postgres treats those NULLs as distinct, SQL Server as equal,
   so the second intake note for a physician-patient pair would collide. Needs a filtered index
   when the PatientNotes module is built.
5. **ETag / 304.** Express sets `app.set('etag','strong')` globally. Not reproduced; belongs in
   middleware.
6. **CS1573 policy.** `Directory.Build.props` sets `GenerateDocumentationFile=true` with
   `NoWarn=1591` only, while the house doc style documents *some* parameters as prose — which is
   exactly what fires CS1573. Either add `1573` to `NoWarn` (matches the deliberate style) or
   complete the tags. The 2× CS1574 (broken `<see cref>`) and 2× CS9124 are worth fixing on
   their own merits either way.
7. **Column widening on `appointments`.** `start_time`, `end_time`, `reason`, `cancel_reason`
   and `recurring_rule` were widened to `nvarchar(max)` to match Prisma's unbounded `text`,
   after a 5-char cap was found to be reachable by ordinary input. None is indexed, but
   `ORDER BY start_time` can no longer use one. Capping at `nvarchar(450)` instead would trade
   fidelity on pathological input for that index — a call worth taking deliberately.
