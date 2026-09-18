# Visits, Appointments, Prescriptions — COMPLETE

**Closed 2026-09-04.** This file used to be the pause marker for the three clinical modules.
All of it is done: 57 handlers, 3 controllers, 57 routes, `dotnet build --no-incremental`
reports **0 errors**.

| Module | Client endpoints | Routed | Extra routed for parity |
|---|---:|---:|---|
| Visits | 20 | 20 | — |
| Appointments | 19 | 20 | `POST /slots` |
| Prescriptions | 14 | 17 | `GET /{id}`, `PUT /{id}`, `GET /interaction-history` |

Every request record pairs 1:1 with exactly one handler (20 / 20 / 17), and no two controller
actions share a verb + route template.

**The durable record now lives in [`PORT-STATUS.md`](PORT-STATUS.md)** — the scoreboard, the
deliberate divergences, the five no-op cross-module ports, and the seven open cross-cutting
decisions. Read that first.

Two module surface inventories were written along the way and are still the fastest way to pick
up either module: [`appointments-surface.md`](appointments-surface.md) and
[`prescriptions-surface.md`](prescriptions-surface.md). Each carries verbatim signatures for
every store method, entity member and cross-module port, the exception-to-status-code table, the
enum member names, and a gaps-and-decisions section.

`docs/port-contracts/` remains the extracted contract for all 53 client-called endpoints.
**The `audit-*.json` files override the `chunk-*.json` files wherever they disagree** — audit-2
alone found 27 contract errors in Appointments, and its own verdict on the chunks was that they
were "not yet safe to port from" unaided.

---

## Findings that shaped the design — still worth reading

**Soft deletes must NOT use a context query filter** in these three modules. See PORT-STATUS.md;
this was built wrong once and corrected.

**`DELETE /api/visits/{id}` does not delete** — it sets `status = 'ARCHIVED'` and never touches
`deleted_at`. The patient keeps access to the archived record.

**`DELETE /api/appointments/{id}` is a HARD delete** (`prisma.appointment.delete`,
appointments.js:948). Nothing in the Node backend ever writes `appointments.deleted_at`. By
contrast `DELETE /slots/{id}` IS soft — it sets `isActive: false`.

**Booking conflicts are EXACT start-time equality, not interval overlap** (appointments.js:450-458),
and are not scoped to the clinic. An 09:00-10:00 booking does NOT block a new 09:30-10:00 one.
An overlap check would reject bookings the Node backend accepts.

**Only PENDING and CONFIRMED hold a slot.** COMPLETED does not.

**`PUT /api/prescriptions/{id}/sign` writes status `CONFIRMED`, not `SIGNED`**
(prescriptions.js:294), and overwrites `signedAt` unconditionally. Rows are created as SIGNED
with `signedAt` already set, so DRAFT is unreachable through any ported endpoint.

**`GET /api/prescriptions/{id}/pdf` returns HTML**, `Content-Type: text/html`, not a PDF binary.

**`POST /api/prescriptions/check-interactions` never has clinical context.** Both halves of its
`Promise.all` (prescriptions.js:823-826) raise `PrismaClientValidationError` — `Allergy` has no
`isActive` column and `ChronicCondition` has `condition`, not `name` — and a bare `catch`
swallows it, so `patientAllergies` and `patientConditions` are `[]` on every request. The port
reproduces the empty result rather than the working query it looks like.

**Every endpoint returning a row needs its OWN DTO.** There is no shared `VisitDto`,
`AppointmentDto` or `PrescriptionDto` that can serve several endpoints: the relation projections
differ per endpoint, several append extra top-level keys to the row (`/check-in` adds three), and
the non-physician branch of `GET /visits` *deletes* seven SOAP keys rather than nulling them.

## Defects found and fixed while porting

- **An infinite loop** in slot generation: a malformed `endTime` yields `NaN`, the comparison
  inverts, and `while(true)` never exits — after `deleteMany` has already committed.
- **The 25 MB audio ceiling on both transcribe routes was unreachable**, because a global 10 MB
  `FormOptions.MultipartBodyLengthLimit` refused the request inside `ReadFormAsync` first. Fixed
  with a per-action `[RequestFormLimits]`; without it, 10–25 MB recordings fail where Node
  transcribes them.
- **`[FromBody] X?` does not make a body optional in MVC.** Nullability is not consulted, so a
  `Content-Length: 0` request answered `400 {"error":"Validation failed"}` where Express hands
  the handler `{}`. Every body with a documented body-less path now carries
  `EmptyBodyBehavior.Allow`.
- **`Mediator.GetInvoker` used `MethodInfo.Invoke`**, which wraps anything a handler throws
  synchronously in `TargetInvocationException` — so a handler validating before its first `await`
  answered **500 instead of 404**, in every module.
- **`UpdatedAt` was never stamped on INSERT**, putting a NULL in `updated_at` where Postgres holds
  a timestamp. Fixed in `BaseDbContext.ApplyAuditing`.
- **Six `appointments` columns were capped narrower than Prisma's `text`**, reachable by ordinary
  input — the slot generator itself emits a 6-character hour past 24:00.
- **Four best-effort `try/catch` blocks were unwrapped**, so a read inside them turned a
  committed write into a 500.
- **Three `??=` timestamps** where Node overwrites unconditionally.
- **`Math.Round` is not `parseFloat(x.toFixed(n))`** — `43/400` gives 0.108 against Node's 0.107.
  The ECMA-262 algorithm is ported exactly in the Appointments read group.
