# Prescriptions — shared surface inventory

Everything the 17 Prescriptions handlers may call, transcribed **verbatim** from the code as it
stands after the surface pass. Four handler files are being written in parallel and none of them
can build, so this document is the contract between them: **if a signature is not here, it does
not exist.**

Built clean at the time of writing:
`dotnet build Tebrazi.Backend.sln --no-incremental -v q --nologo` → **0 errors, 216 warnings**
(down from 224 before this pass — the eight removed are the `CS1573` instances on
`PrescriptionFilter`, whose `<param>` set is now complete. Every remaining warning is pre-existing
`CS1573`/`CS1574`/`CS9124` in files this pass did not touch; nothing in this inventory emits one).

Sections:

1. [`IPrescriptionStore` / `IInteractionAlertStore`](#1-store-surface)
2. [`Prescription` / `InteractionAlert`](#2-entity-surface)
3. [Cross-module ports](#3-cross-module-ports)
4. [`RxDocumentRenderer`](#4-rxdocumentrenderer)
5. [`IAiGateway`](#5-iaigateway)
6. [Caller context, pagination and committing](#6-caller-context-pagination-and-committing)
7. [Exception → status → body](#7-exception--status--body)
8. [Enums](#8-enums)
9. [The `using` list for a new use-case file](#9-using-list-for-a-new-use-case-file)
10. [Name collisions across the four files](#10-name-collisions-across-the-four-files)
11. [Gaps and decisions](#11-gaps-and-decisions)
12. [Reusable helpers — what exists, what you cannot reach](#12-reusable-helpers)

Endpoint → group map, so you know which handler you own:

| Group | Prefix | Endpoints |
|---|---|---|
| A — read | `RxRead*` | `GET /`, `GET /summary`, `GET /{id}`, `GET /{id}/pdf`, `GET /interaction-history` |
| B — lifecycle | `RxLifecycle*` | `POST /`, `PUT /{id}`, `PUT /{id}/sign`, `PUT /{id}/send`, `PUT /{id}/dispense` |
| C — refill & medication | `RxRefill*` | `POST /{id}/refill-request`, `PUT /{id}/refill-respond`, `PUT /{id}/stop-medication`, `PUT /{id}/resume-medication` |
| D — AI | `RxAi*` | `POST /check-interactions`, `POST /extract-from-plan`, `POST /transcribe-rx` |

---

## 1. Store surface

`Tebrazi.Prescriptions.Application.Abstractions.Persistence` —
`Modules/Prescriptions/Tebrazi.Prescriptions.Application/Abstractions/Persistence/IPrescriptionStores.cs`

### Supporting types

```csharp
public interface IPrescriptionsDbContext : IDbContext;

public sealed record PrescriptionFilter(
    string? PhysicianId = null,
    string? VisitId = null,
    IReadOnlyCollection<string>? VisitIds = null,
    string? SubprofileId = null,
    PrescriptionStatus? Status = null,
    IReadOnlyCollection<PrescriptionStatus>? StatusIn = null,
    string? RefillStatus = null,
    DateTime? From = null,
    DateTime? To = null,
    bool? IsDeleted = null);
```

A **null** member is not applied, matching how the Node handler builds its `where` key by key.

* `PhysicianId` — the author's physician **profile** id, not a user id.
* `VisitId` — one visit, exact equality, no uuid validation.
* `VisitIds` — a set. **An empty collection matches nothing**, deliberately: it means "the patient
  has no visits". Treating it as unfiltered leaks the whole table.
* `StatusIn` — same rule; empty matches nothing.
* `RefillStatus`, `From`, `To` — **not used by any of the 17 endpoints.** Do not reach for them.
* `IsDeleted` — `null` = no filter, `false` = live only, `true` = deleted only.
  **All 17 endpoints leave it null.** See §11.

> Pass `null`, not `""`, for an absent filter. ASP.NET binds a present-but-valueless query key
> (`?visitId=`) to `""`, and every store test is `is not null`, so `""` would filter on the empty
> id and return nothing where Node returns everything. Use `RxJs.Truthy(...)` (§12) in the handler.

### `IPrescriptionStore`

```csharp
Task<Prescription?> GetForUpdateAsync(string id, CancellationToken ct = default);
```
Tracked single row by id. **No `DeletedAt` filter.** This is the load for `/sign`, `/send`,
`/dispense`, `PUT /{id}`, both refill routes and both medication routes — every one of which is a
bare `findUnique({ where: { id } })` in Node.

```csharp
Task<Prescription?> GetAsync(string id, CancellationToken ct = default);
```
Untracked single row by id, for `GET /{id}` and `GET /{id}/pdf`. **No `DeletedAt` filter**
(prescriptions.js:193, :878) — a soft-deleted prescription still renders a full document.

```csharp
Task<(IReadOnlyList<Prescription> Items, int TotalCount)> PageAsync(
    PrescriptionFilter filter, int page, int pageSize, CancellationToken ct = default);
```
Ordered `created_at` DESC. **Not used by any of the 17 endpoints** — `GET /api/prescriptions`
returns a bare JSON array with no pagination envelope. Use `ListAsync`.

```csharp
Task<IReadOnlyList<Prescription>> ListAsync(
    PrescriptionFilter filter, CancellationToken ct = default);
```
The whole filtered set, ordered `created_at` **DESC** — which is `GET /`'s own
`orderBy: { createdAt: 'desc' }` (prescriptions.js:66). No `DeletedAt` filter unless
`filter.IsDeleted` is set. This is the one method `GET /` needs, on both branches:

* physician: `new PrescriptionFilter(PhysicianId: profile.Id, VisitId: RxJs.Truthy(visitId))`
* patient: `new PrescriptionFilter(VisitIds: visitIds, StatusIn: [CONFIRMED, SENT, DISPENSED], VisitId: RxJs.Truthy(visitId))`

```csharp
Task<IReadOnlyList<Prescription>> ListForVisitAsync(string visitId, CancellationToken ct = default);
```
One visit, newest first. Used by the Visits module's read, by no prescriptions route. No
`DeletedAt` filter.

```csharp
Task<IReadOnlyDictionary<string, List<Prescription>>> ListForVisitsAsync(
    IReadOnlyCollection<string> visitIds, CancellationToken ct = default);
```
Grouped by visit id, one query, newest first inside each group. A visit with none is **absent**
from the dictionary. No `DeletedAt` filter. No prescriptions route needs it.

```csharp
Task<IReadOnlyList<Prescription>> ListForInteractionCheckAsync(
    IReadOnlyCollection<string> visitIds,
    IReadOnlyCollection<PrescriptionStatus> statuses,
    CancellationToken ct = default);
```
**NEW this pass.** The cross-prescription drug sweep for `POST /check-interactions`
(prescriptions.js:768-774). Pass `[SIGNED, CONFIRMED, SENT, DISPENSED]` — note **SIGNED is
included here**, unlike the patient list filter, so a never-sent prescription contributes its
drugs. No `DeletedAt` filter. Empty `visitIds` **or** empty `statuses` returns `[]`.

Ordered `created_at` **ASC**, and that is separate from `ListAsync` on purpose: Node's `findMany`
has no `orderBy` at all, so PostgreSQL returns physical order — effectively insertion order for
this append-only table — and the order is **observable**, because it decides the sequence of
`drugsChecked` in the response.

```csharp
Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(
    string physicianId, CancellationToken ct = default);
```
One `GROUP BY`. **No `DeletedAt` filter** — Node's counts include soft-deleted rows
(prescriptions.js:150-154). Keys are `PrescriptionStatus` member names; **a status with no rows is
absent, not zero** — read with `GetValueOrDefault`.

All three of `GET /summary`'s numbers come out of this one call, which is why there is no second
count method:

| Response key | Compute as | Why |
|---|---|---|
| `signed` | `CONFIRMED + SIGNED` | The Node filter is `status: { in: ['CONFIRMED','SIGNED'] }` — **two statuses**. Creation writes SIGNED, `/sign` writes CONFIRMED, both belong here. |
| `sent` | `SENT` | — |
| `total` | **sum of every value** | Counts all statuses including DRAFT and DISPENSED, so `signed + sent != total`. |

```csharp
void Add(Prescription prescription);
```

```csharp
void MarkModified(Prescription prescription);
```
**NEW this pass.** Forces the next `SaveChangesAsync` to emit an UPDATE with no property change,
so `updated_at` is stamped. Needed by **exactly one** endpoint: `PUT /{id}` builds its patch with
`!== undefined` guards, so an empty body `{}` issues `prisma.update({ data: {} })`, which still
succeeds and still bumps `updatedAt`. EF would write nothing. Call it only for that no-op-patch
case; it rewrites every column with its current value.

### `IInteractionAlertStore`

```csharp
void Add(InteractionAlert alert);

Task<IReadOnlyList<InteractionAlert>> ListForPhysicianAsync(
    string physicianId, int take, CancellationToken ct = default);
```
`ListForPhysicianAsync` is **NEW this pass** — the store was write-only, and
`GET /interaction-history` is now in scope. Ordered `checked_at` DESC. Filters on `physician_id`
**only**: the route answers `200 []` for any caller without a physician profile — i.e. every
patient — without issuing a query, even though patient-authored rows exist in the table
(prescriptions.js:171). Do not add a patient read.

`take` comes from `parseInt(req.query.limit) || 20`, so it is **not clamped and not capped**, and
**a negative value is meaningful**: Prisma reads `take: -5` as reverse-take — the last five rows of
the `checkedAt desc` ordering, i.e. the five *oldest* alerts, still newest-first among themselves.
The implementation reproduces that by ordering ascending, taking `|take|`, and reversing.
`take == 0` returns `[]` (unreachable from the query string, since `|| 20` catches it).

There is no `DeletedAt` on this entity at all.

---

## 2. Entity surface

`Tebrazi.Prescriptions.Domain.Entities` —
`Modules/Prescriptions/Tebrazi.Prescriptions.Domain/Entities/`

### `Prescription : MutableEntity<string>`

Sixteen scalars, and **all sixteen appear on the wire** for `GET /`, `POST /`, `PUT /{id}`,
`/sign`, `/send` and `/dispense`. Inherited members come from `MutableEntity<string>` →
`ImmutableEntity<string>` → `Entity<string>`.

| Member | Type | Notes |
|---|---|---|
| `Id` | `string` | Inherited. A uuid string, assigned by the factory, never DB-generated. |
| `VisitId` | `string` | Required. Plain column, **no navigation** — the visit lives in Visits. |
| `PhysicianId` | `string` | Required. The author's physician **profile** id. |
| `SubprofileId` | `string?` | Inherited from the VISIT on create, never from the request body. |
| `Status` | `PrescriptionStatus` | Persists and serializes **as a string**. |
| `Medications` | `string` | Required. **Opaque JSON text, echoed verbatim.** May be any JSON value, including a scalar. |
| `Notes` | `string?` | `notes \|\| null` on create, so `""` is stored as NULL. |
| `SignedAt` | `DateTime?` | Set at CREATION (auto-sign) and overwritten by `/sign`. |
| `SentToPatientAt` | `DateTime?` | Set by `/send` only. |
| `PdfUrl` | `string?` | Never written by any of the 17 endpoints. Always null in practice. |
| `RefillRequestedAt` | `DateTime?` | Set by refill-request; **never cleared** by refill-respond. |
| `RefillStatus` | `string?` | Free text, **not an enum**: `"PENDING"` / `"APPROVED"` / `"DENIED"`, null when never requested. |
| `RefillNotes` | `string?` | See the asymmetry on the two refill methods below. |
| `DeletedAt` | `DateTime?` | **Nothing filters on it.** No context query filter, and no route mentions it. |
| `CreatedAt` | `DateTime` | Inherited; stamped by `BaseDbContext`. |
| `CreatedBy` | `string` | Inherited; `ICurrentUser.UserId ?? "SYSTEM"`. Not on the wire. |
| `UpdatedAt` | `DateTime?` | Inherited, and **null until the first modification** — `BaseDbContext` stamps it only for `EntityState.Modified`. Prisma's `@updatedAt` (schema.prisma:693) is NON-null from creation, so **every response DTO must declare `DateTime UpdatedAt` and map `UpdatedAt ?? CreatedAt`**, exactly as the finished Visits module already does for this same row (`VisitReadResponses.cs:479`). Do NOT expose the raw nullable. |
| `UpdatedBy` | `string?` | Inherited. Not on the wire. |

Factories and behaviour — **verbatim signatures**:

```csharp
public static Prescription Create(
    string visitId,
    string physicianId,
    string medications,
    string? subprofileId = null,
    string? notes = null,
    PrescriptionStatus status = PrescriptionStatus.DRAFT);
```
Defaults to DRAFT and **does not stamp `SignedAt`**. No ported endpoint should call it — a DRAFT
row is unreachable through the API and is invisible to the patient list. Throws
`ArgumentException` for a null/blank `visitId`, `physicianId` or `medications`.

```csharp
public static Prescription CreateAutoSigned(
    string visitId,
    string physicianId,
    string medications,
    string? subprofileId = null,
    string? notes = null);
```
**NEW this pass.** What `POST /api/prescriptions` actually creates (prescriptions.js:118-131):
status **SIGNED** with `SignedAt` already stamped, because the physician's authenticated account
implicitly signs at creation. `subprofileId` comes from the **visit**, resolved through
`IVisitDirectory`. `notes` must already be through `notes || null`.

```csharp
public void ReplaceMedications(string medications);
```
Writes the JSON text verbatim. **No status gate in either backend** — the old doc comment claiming
"only legal while a draft" was wrong and has been corrected. `PUT /{id}` must apply the SENT
precondition itself. Throws `ArgumentException` on null/empty/whitespace; it **cannot** reject the
four-character text `"null"`, so a handler receiving an explicit `medications: null` must raise
its own 500 rather than call this (see §11).

```csharp
public void SetNotes(string? notes);
```
Null clears the column. `PUT /{id}` guards with `!== undefined`, so an explicit `notes: null`
wipes and an **absent** key preserves — distinguish `JsonValueKind.Undefined` from
`JsonValueKind.Null` and only call this for the latter.

```csharp
public void Sign();
```
Writes status **`CONFIRMED`**, not `SIGNED` (prescriptions.js:294), and overwrites `SignedAt`
**unconditionally** — re-signing loses the original timestamp. No status precondition: signing a
SENT or DISPENSED prescription regresses it to CONFIRMED while leaving `SentToPatientAt`
populated. That is the contract.

```csharp
public void SendToPatient();
```
Writes status `SENT` and `SentToPatientAt`. **Does not touch `SignedAt`.** No DRAFT guard — the
Node comment "Auto-sign is now done at creation" documents its removal, so a DISPENSED row can be
re-sent and regresses to SENT.

```csharp
public void Dispense();
```
Status `DISPENSED` only. No precondition; dispensing twice re-runs the deduction.

```csharp
public void SetPdfUrl(string? pdfUrl);
```
Not called by any of the 17 endpoints. `GET /{id}/pdf` renders HTML on the fly and stores nothing.

```csharp
public void RequestRefill(string? notes = null);
```
`RefillRequestedAt = now`, `RefillStatus = "PENDING"`, and **`RefillNotes` is overwritten
unconditionally, including to null** — Node writes `refillNotes: notes || null`, so a request with
no notes silently wipes the physician's previous decision note. Pass `RxJs.Truthy(notes)`.
**Changed this pass**: it previously preserved on null, which was wrong.

```csharp
public void RespondToRefill(string status, string? notes = null);
```
`RefillStatus = status` (the raw uppercase string, verbatim into the free-text column), and
**`RefillNotes` is PRESERVED when `notes` is null** — `refillNotes: notes || prescription.refillNotes`.
The exact opposite of `RequestRefill`; there is no way to clear the notes here. Pass
`RxJs.Truthy(notes)`. `RefillRequestedAt` is left alone. Throws `ArgumentException` for a
null/blank status.

```csharp
public void SoftDelete();
```
`DeletedAt ??= now`. **No endpoint calls it** — there is no delete route in `prescriptions.js`.

```csharp
public enum PrescriptionStatus { DRAFT, SIGNED, CONFIRMED, SENT, DISPENSED }
```

### `InteractionAlert : ImmutableEntity<string>`

| Member | Type | Notes |
|---|---|---|
| `Id` | `string` | Inherited. |
| `PhysicianId` | `string?` | The physician **profile** id, or null when a patient ran the check. |
| `PatientUserId` | `string?` | The patient's **user** id, or null when a physician ran the check. Exactly one of the two is set. |
| `VisitId` | `string?` | The column exists but `check-interactions` **never writes it**, so it is always null in practice. |
| `Drugs` | `string` | Required. Opaque JSON — the `allDrugs` array, which can legally contain non-string objects. |
| `Interactions` | `string` | Required, defaults to `"[]"`. Opaque JSON straight from the gateway. |
| `AlertCount` | `int` | `result.interactions.length`. |
| `CheckedAt` | `DateTime` | The Prisma field, **distinct from `CreatedAt`** — the API returns `checkedAt` and never `createdAt`. |
| `CreatedAt` | `DateTime` | Inherited; not on the wire. |
| `CreatedBy` | `string` | Inherited; not on the wire. |

```csharp
public static InteractionAlert Create(
    string drugs,
    string? interactions = null,
    int alertCount = 0,
    string? physicianId = null,
    string? patientUserId = null,
    string? visitId = null,
    DateTime? checkedAt = null);
```
A null/blank `interactions` becomes `"[]"`. `checkedAt` defaults to `DateTime.UtcNow`. Throws
`ArgumentException` for a null/blank `drugs`.

`GET /interaction-history` echoes the raw row with **no reshaping** — element keys in Prisma model
order: `id, physicianId, patientUserId, visitId, drugs, interactions, alertCount, checkedAt`.
`drugs` and `interactions` must be emitted as **raw JSON**, not as escaped strings; bind them into
your DTO as `JsonNode`/`JsonElement` parsed from the stored text.

---

## 3. Cross-module ports

Every one is registered by `AddInfrastructureShared()` or by its owning module, so a handler just
constructor-injects it. **No module ever injects another module's DbContext.**

### 3.1 `Tebrazi.SharedKernel.Abstractions.Directory.IIdentityDirectory`

```csharp
Task<UserSummary?> GetUserAsync(string userId, CancellationToken ct = default);
Task<UserSummary?> GetUserByEmailAsync(string email, string? userType = null, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, UserSummary>> GetUsersAsync(
    IReadOnlyCollection<string> userIds, CancellationToken ct = default);
Task<PhysicianSummary?> GetPhysicianByUserIdAsync(string userId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, PhysicianSummary>> GetPhysiciansAsync(
    IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default);
Task<PhysicianSummary?> GetPhysicianAsync(string physicianProfileId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, PhysicianDetail>> GetPhysicianDetailsAsync(
    IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default);
Task<string?> GetSubscriptionTierAsync(string userId, CancellationToken ct = default);
```

```csharp
public sealed record UserSummary(
    string Id, string? Email, string DisplayName, string? Phone,
    string? ProfilePictureUrl, string Role, string UserType, bool Active);

public sealed record PhysicianSummary(
    string Id, string UserId, string LicenseNumber, string Specialty,
    bool Verified, string DisplayName);

public sealed record PhysicianDetail(
    string Id, string UserId, string LicenseNumber, string Specialty,
    string? Qualifications, string? Bio, string? ScratchpadNotes,
    int? YearsOfExperience, bool Verified, DateTime? VerifiedAt,
    DateTime CreatedAt, DateTime? UpdatedAt, string DisplayName);
```

This is the single most-used port here — **thirteen of the seventeen endpoints** open with
`GetPhysicianByUserIdAsync(callerUserId)`. Note `PhysicianSummary` carries `DisplayName` (the
owner's), which is what `/send`'s notification and `/pdf`'s letterhead need, so a separate
`GetUserAsync` is usually unnecessary. `GetPhysicianAsync` takes the **profile** id — that is what
refill-request needs to turn `prescription.PhysicianId` into the physician's user id for the
notification (prescriptions.js:560).

`GetUsersAsync` is the batched lookup behind `GET /`'s `patientName` enrichment; ids that do not
resolve are **absent** from the dictionary, which is exactly what makes the key drop out of the
JSON (see §11). `GetSubscriptionTierAsync` exists for the AI quota gate that is **not ported** —
do not call it.

Failure guarantee: read-only, throws on infrastructure failure. Nothing degrades.

### 3.2 `Tebrazi.SharedKernel.Abstractions.Directory.IVisitDirectory`

```csharp
Task<VisitSummary?> GetAsync(string visitId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, VisitSummary>> GetManyAsync(
    IReadOnlyCollection<string> visitIds, CancellationToken ct = default);
Task<IReadOnlyList<string>> ListVisitIdsForPatientAsync(
    string patientUserId, string? subprofileId = null, CancellationToken ct = default);
Task<bool> BelongsToPhysicianAsync(string visitId, string physicianId, CancellationToken ct = default);
Task<int> CountCompletedAsync(string clinicId, string patientUserId, CancellationToken ct = default);
Task<IReadOnlyList<VisitQueueRow>> ListForClinicDayAsync(
    string clinicId, DateTime dayStartUtc, DateTime dayEndUtc, CancellationToken ct = default);
Task<IReadOnlyList<VisitDuration>> ListCompletedDurationsAsync(
    string physicianId, DateTime since, CancellationToken ct = default);
```

```csharp
public sealed record VisitSummary(
    string Id, string OrganizationId, string ClinicId, string PhysicianId,
    string PatientUserId, string? SubprofileId, string? ClinicPatientId,
    DateTime VisitDate, string Status, string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis, string? Plan);

public sealed record VisitQueueRow(
    string Id, string PatientUserId, string? SubprofileId, string? ClinicPatientId,
    string Status, DateTime VisitDate, DateTime? FollowUpDate,
    string? FollowUpNotes, DateTime? CompletedAt);

public sealed record VisitDuration(DateTime VisitDate, DateTime CompletedAt);
```

The port that makes a patient-scoped prescription list possible at all — `prescriptions` has a
`visit_id` and no patient column. **An empty `ListVisitIdsForPatientAsync` result is a filter that
matches nothing, not "no filter".**

Which methods each endpoint wants:

* `GET /` patient branch → `ListVisitIdsForPatientAsync(callerUserId)` (leave `subprofileId` null;
  the Node relational filter is `visit: { patientUserId }` with no dependant narrowing), then
  `GetManyAsync` for the embedded `visit` objects.
* `POST /` → `BelongsToPhysicianAsync(visitId, profile.Id)` for the 403, then `GetAsync` for
  `visit.SubprofileId`, `ChiefComplaint` and `VisitDate`. Note Node uses **one** `findFirst`
  with both conditions and 403s on a miss — a nonexistent visit id is also 403, never 404.
* `GET /{id}`, `GET /{id}/pdf`, `/send`, refill and medication routes → `GetAsync(prescription.VisitId)`
  for `PatientUserId` (the ownership test) and, for `/pdf`, `ClinicId` / `ClinicPatientId`.
* `POST /extract-from-plan` → **nothing.** `planText` comes from the body; `VisitSummary.Plan`
  exists for a Visits endpoint, not this one.

`ListForClinicDayAsync`, `ListCompletedDurationsAsync` and `CountCompletedAsync` belong to
Appointments. No prescriptions endpoint uses them.

> Divergence to be aware of, not to fix here: Node's patient list is a *relational* filter
> (`where.visit = { patientUserId }`) evaluated inside one query, whereas this resolves ids first.
> If the Visits implementation of `ListVisitIdsForPatientAsync` excludes soft-deleted visits, the
> two backends differ for a prescription on a deleted visit. Report it rather than editing Visits.

### 3.3 `Tebrazi.SharedKernel.Abstractions.Directory.IClinicDirectory`

```csharp
Task<ClinicSummary?> GetClinicAsync(string clinicId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, ClinicSummary>> GetClinicsAsync(
    IReadOnlyCollection<string> clinicIds, CancellationToken ct = default);
Task<bool> IsOwningPhysicianAsync(string clinicId, string physicianProfileId, CancellationToken ct = default);
Task<bool> IsActiveStaffAsync(string userId, string clinicId, CancellationToken ct = default);
Task<IReadOnlyList<string>> ListActiveStaffUserIdsAsync(string clinicId, CancellationToken ct = default);
```

```csharp
public sealed record ClinicSummary(
    string Id, string OrganizationId, string PhysicianId, string Name,
    string? Address, string? City, string? Country, string? Phone, string? Email,
    string? Specialty, string? Logo, string? WorkingHours, bool IsActive,
    bool AllowPatientBooking, double? ConsultationFee, double? FollowUpFee);
```

Two prescriptions endpoints need it, both read-only: `GET /` and `GET /{id}` embed
`visit.clinic` (name, plus phone/address on `/{id}`), and `GET /{id}/pdf` needs
`Name, Address, City, Phone, Logo`. `Visit.clinicId` is a required column, so
**`clinic` is never null on the wire** — the Node `|| { name: 'Clinic' }` fallback is dead code.

**Do not apply any access test from this port.** No prescriptions route consults clinic standing;
the only gates are "is the caller the authoring physician" and "is the caller the visit's patient".
`IsOwningPhysicianAsync` / `IsActiveStaffAsync` / `IClinicAccessEvaluator` would all grant access
Node does not.

### 3.4 `Tebrazi.SharedKernel.Abstractions.Directory.IPatientDirectory`

```csharp
Task<SubprofileSummary?> GetSubprofileAsync(string subprofileId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, SubprofileSummary>> GetSubprofilesAsync(
    IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default);
Task<SubprofileClinicalContext?> GetSubprofileClinicalContextAsync(
    string subprofileId, CancellationToken ct = default);
Task<IReadOnlyList<string>> ListSubprofileIdsByUserAsync(
    string patientUserId, CancellationToken ct = default);
Task<IReadOnlyList<string>> ListActiveMedicationNamesAsync(
    IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default);
```

```csharp
public sealed record SubprofileSummary(
    string Id, string PatientProfileId, string Name, string Relation,
    DateTime? DateOfBirth, string? Gender, string? BloodType, bool IsActive);

public sealed record SubprofileClinicalContext(
    string Id, string Name, string Relation,
    IReadOnlyList<AllergyItem> Allergies,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<MedicationItem> Medications);

public sealed record AllergyItem(string Allergen, string? Severity, string? Reaction);
public sealed record MedicationItem(string DrugName, string? Dosage, string? Frequency);
```

`Relation` is a **string** on the wire — `SELF | SPOUSE | CHILD | PARENT | SIBLING | OTHER`, raw
and uppercase. `GET /` and `GET /{id}` embed `{ name, relation }` (+ `dateOfBirth` on `/{id}`);
`/pdf` renders `"{Name} ({Relation})"` with the raw enum.

`POST /check-interactions` needs three of these:
`ListActiveMedicationNamesAsync([subprofileId])` for its source 1,
`ListSubprofileIdsByUserAsync(patientUserId)` + `ListActiveMedicationNamesAsync(thoseIds)` for the
family sweep (which runs **only when `subprofileId` is falsy**), and
`GetSubprofileClinicalContextAsync` for the allergy/condition AI context. Note the Node context
lookup picks `subprofileId || (unordered findFirst for the patient)`, so on a multi-dependant
account **the allergies may belong to an arbitrary family member**, and the whole block sits in a
bare `catch {}` that continues with empty lists.

### 3.5 `Tebrazi.SharedKernel.Abstractions.Directory.IClinicPatientDirectory`

```csharp
Task<ClinicPatientSummary?> GetAsync(string clinicPatientId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, ClinicPatientSummary>> GetManyAsync(
    IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default);
Task TouchLastVisitAsync(string clinicPatientId, DateTime visitedAtUtc, CancellationToken ct = default);
```

```csharp
public sealed record ClinicPatientSummary(
    string Id, string PhysicianUserId, string? ClinicId, string Name,
    string? Phone, string? Email, DateTime? DateOfBirth, string? Gender,
    string? BloodType, string? LinkedUserId, bool IsActive);
```

Needed by **`GET /{id}/pdf` only**, for tier 2 of the patient-name fallback
(`visit.clinicPatient?.name`). Never call `TouchLastVisitAsync` from this module.

### 3.6 `Tebrazi.SharedKernel.Abstractions.INotificationPublisher`

```csharp
Task PublishAsync(NotificationRequest request, CancellationToken ct = default);
Task PublishAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default);

public sealed record NotificationRequest(
    string UserId, string Type, string Title, string Message,
    string? Data = null, bool SendEmail = false);
```

**Failure guarantee: best-effort. This port NEVER throws.** Every Node call site wraps
`createNotification` in its own try/catch. Do not add your own try/catch around it, and do not
treat a completed call as proof a row was written. Call it **after** the transaction returns —
it commits through the Notifications unit of work and is not covered.

`Data` is opaque JSON **text**; every prescriptions call site passes
`{"prescriptionId":"<the raw :id route segment>"}`.

The four notifications this module raises:

| Endpoint | Type | Title | Message | `SendEmail` |
|---|---|---|---|---|
| `/send` #1 | `PRESCRIPTION_SENT` | `New Prescription Available` | `Dr. {displayName or 'Your doctor'} has sent you a new prescription. View it in your Prescriptions page.` | **true** |
| `/send` #2 | `MEDICATION_REMINDER_SET` | `Medication Reminders Set` | `{n} medication reminder{'s have' or ' has'} been automatically created from your new prescription.` — only when `n > 0` | false |
| refill-request | `REFILL_REQUEST` | `Prescription Refill Request` | `{patient displayName or 'A patient'} requested a prescription refill.` | false |
| refill-respond | `REFILL_RESPONSE` | `Refill Approved` / `Refill Denied` | `Your prescription refill request has been {approved\|denied}.{' Note: ' + notes when notes is truthy}` | **true** |

`"Dr. "` is hardcoded, so a display name already containing a title renders `Dr. Dr. Ahmed`.
Reproduce it. Refill-respond's note suffix is `' Note: ' + notes` — one leading space after the
full stop, no comma.

### 3.7 `Tebrazi.SharedKernel.Abstractions.IMedicationReminderScheduler` — NEW, no-op

`Kernel/Tebrazi.SharedKernel/Abstractions/IMedicationReminderScheduler.cs`

```csharp
Task<int> ScheduleAsync(MedicationReminderPlan plan, CancellationToken ct = default);

public sealed record MedicationReminderPlan(
    string PatientUserId,
    string CreatedByUserId,
    string PrescriptionId,
    IReadOnlyList<MedicationReminderGroup> Medications);

public sealed record MedicationReminderGroup(
    string DrugName,
    IReadOnlyList<MedicationReminderRow> Doses);

public sealed record MedicationReminderRow(
    string Title,
    string Description,
    DateTime RemindAt,
    string Notes,
    string Type = "MEDICATION",
    string Recurrence = "DAILY");
```

Owner: the **Reminders** module. Placeholder:
`Kernel/Tebrazi.Infrastructure.Shared/Placeholders/UnimplementedMedicationReminderScheduler.cs`,
which logs at warning level naming the prescription, patient, drug count and dose count, and
returns `0`.

Called by **`PUT /{id}/send` only** (prescriptions.js:347-410).

* `PatientUserId` — from the visit, **not** the caller. Node runs the whole block only when it is
  truthy, so do not call at all without one.
* `CreatedByUserId` — the **sending physician's user id**. (`reminders.patient_user_id` is left
  NULL by Node despite the schema comment; that is the implementation's business.)
* `PrescriptionId` — the raw `:id` route segment.
* `Medications` — one group per medication that has **both** a `drugName` and a `frequency`;
  everything else is skipped outright, contributing neither a purge nor a row.
* `DrugName` — the **purge key**: a case-sensitive `contains` against existing reminder titles, so
  it also wipes that drug's ACTIVE reminders from *other* prescriptions, and a short name that is
  a substring of another drug's name wipes that drug's too.

Build the groups with `MedicationSchedule.BuildGroup(...)` (§12) — do not compose the titles,
descriptions, `RemindAt` or `Notes` by hand.

**Failure guarantee: BEST-EFFORT. It never throws.** Return value drives *your* side effect: raise
notification #2 only when it is `> 0`. It returns **0 on any failure and never a partial count**,
because Node's `if (remindersCreated > 0)` sits inside the same try block a mid-loop throw escapes
— a partial count would fire a notification Node would not.

### 3.8 `Tebrazi.SharedKernel.Abstractions.IInventoryDeductionWriter` — NEW, no-op

`Kernel/Tebrazi.SharedKernel/Abstractions/IInventoryDeductionWriter.cs`

```csharp
Task<IReadOnlyList<InventoryDeductionLine>?> DeductForPrescriptionAsync(
    InventoryDeductionRequest request, CancellationToken ct = default);

public sealed record InventoryDeductionRequest(
    string PrescriptionId,
    string PerformedByUserId,
    IReadOnlyList<InventoryDeductionItem> Items);

public sealed record InventoryDeductionItem(string ItemId, int? Quantity);

public sealed record InventoryDeductionLine(
    string ItemId,
    string Name,
    int Quantity,
    int NewStock);
```

Owner: the **Inventory** module. Placeholder:
`Kernel/Tebrazi.Infrastructure.Shared/Placeholders/UnimplementedInventoryDeductionWriter.cs`,
which logs at warning level naming the prescription and the item ids it did not decrement, and
returns an **empty list**.

Called by **`PUT /{id}/dispense` only** (prescriptions.js:464-508). `InventoryDeductionLine` is
exactly the four keys of an `inventoryDeducted` element — note `itemId`, not `id`, and note
`Quantity` is the **positive** number while the transaction row stores its negation.

Caller responsibilities, because they are dispense-contract quirks rather than inventory ones:

* Only process when the body's `inventoryItems` `Array.isArray(...) && length > 0`; any other value
  (object, string, null, absent) is silently ignored and the response is `[]`.
* Apply the truthiness skip yourself: `if (!itemId || !quantity) continue` — quantity `0`, `""`,
  `null`, `false` all drop the item with no error and no response entry.
* Apply `parseInt(quantity)` yourself (`RxJs.ParseInt(JsonElement)`), and pass `Quantity: null`
  when it yields NaN. A negative quantity is legal and **increases** stock.

**Failure guarantee: LOAD-BEARING.** The result IS part of the 200 body, and Node does **not**
wrap the inventory loop in a try/catch of its own, so a failure becomes
`500 {"error":"Failed to dispense prescription"}` with the prescription **already DISPENSED** —
the status write comes first, at prescriptions.js:458-461, and must stay first.

* `null` → answer that 500. Do not substitute `[]`.
* `[]` → a normal 200. Node genuinely produces it on several paths.
* A thrown exception is equally acceptable and lands on the same 500 through your file guard.

### 3.9 The other placeholder ports

`IWaitingQueueWriter`, `IPaymentWriter`, `IPatientNoteWriter` (all in
`Tebrazi.SharedKernel.Abstractions`) exist and are registered, but **no prescriptions endpoint
touches any of them.** Do not inject them.

### 3.10 `Tebrazi.SharedKernel.Abstractions.Directory.IPrescriptionDirectory`

This module's own **outbound** port, implemented by `PrescriptionDirectory` for the Visits module.
Nothing in this module should inject it — use `IPrescriptionStore` instead.

---

## 4. `RxDocumentRenderer`

`Tebrazi.Prescriptions.Application.Services` —
`Modules/Prescriptions/Tebrazi.Prescriptions.Application/Services/RxDocumentRenderer.cs`

A faithful port of `generateRxPDFHtml` (`server/src/services/pdfService.js`). Pure static, no I/O.
**It returns HTML, not a PDF** — `GET /{id}/pdf` must set `Content-Type: text/html; charset=utf-8`
and send this string. Errors on that route stay JSON.

```csharp
public static class RxDocumentRenderer
{
    public static string RenderHtml(RxDocument document);
    public static IReadOnlyList<RxMedication> ParseMedications(string? medicationsJson);
}
```

```csharp
public sealed record RxDocument(
    string PrescriptionId,
    DateTime? SignedAt,
    DateTime CreatedAt,
    string? Notes,
    string? ClinicName,
    string? ClinicAddress,
    string? ClinicCity,
    string? ClinicPhone,
    string? ClinicLogo,
    string PhysicianName,
    string? PhysicianSpecialty,
    string? PhysicianLicenseNumber,
    string PatientName,
    IReadOnlyList<RxMedication> Medications);

public sealed record RxMedication(
    string? DrugName,
    string? Dosage,
    string? Frequency,
    string? Duration,
    string? Instructions);
```

How to populate it — the renderer performs **no lookups of its own**:

| Field | Source |
|---|---|
| `PrescriptionId` | `prescription.Id`. The renderer takes `[..8].ToUpperInvariant()` for the `Rx #`. |
| `SignedAt` / `CreatedAt` | The row's own. The printed date is `SignedAt ?? CreatedAt`, formatted `dd MMM yyyy` en-GB against **server-local** time, and it appears in both the patient bar and the signature line — including for an unsigned row, which still claims "Electronically signed". |
| `Notes` | `prescription.Notes`. The notes block is omitted entirely when empty. |
| `Clinic*` | `IClinicDirectory.GetClinicAsync(visit.ClinicId)` → `Name, Address, City, Phone, Logo`. |
| `PhysicianName` | `PhysicianSummary.DisplayName`. The template prefixes `Dr. ` itself. |
| `PhysicianSpecialty` / `PhysicianLicenseNumber` | `PhysicianSummary`. The licence line is omitted when empty. |
| `PatientName` | The three-tier fallback, **in this precedence**: (1) `"{subprofile.Name} ({subprofile.Relation})"` with the raw uppercase relation — **this wins even when a clinic chart exists**; (2) `ClinicPatientSummary.Name`; (3) `UserSummary.DisplayName` for `visit.PatientUserId`; (4) the literal `"Patient"`. |
| `Medications` | `RxDocumentRenderer.ParseMedications(prescription.Medications)`. |

`ParseMedications` returns `[]` for null, whitespace, invalid JSON, a non-array value, or an array
whose elements are not objects — never throws. Non-string property values are read as null.
Stored order is preserved and **stopped medications still print**.

Inside the renderer, already handled for you: the exact `escapeHtml` entity set
(`& < > " '` → `&amp; &lt; &gt; &quot; &#39;`, non-ASCII untouched), the `|| '—'` em-dash fallback
on dosage/frequency/duration, the drug name's `|| ''` fallback with **no** dash, the
always-emitted empty `<p></p>` when address and city are both null, the `"Clinic"` name fallback,
and the `🖨` / `℞` emoji. Serve it as UTF-8.

---

## 5. `IAiGateway`

`Tebrazi.SharedKernel.Abstractions` — `Kernel/Tebrazi.SharedKernel/Abstractions/IAiGateway.cs`

```csharp
public interface IAiGateway
{
    Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken ct = default);
    Task<AiTranscriptionResult> TranscribeAsync(AiTranscriptionRequest request, CancellationToken ct = default);
    Task<AiDrugCheckResult> CheckDrugsAsync(AiDrugCheckRequest request, CancellationToken ct = default);
    bool IsConfigured { get; }
}

public sealed record AiChatRequest(
    string System, string User, string? Model = null, double? Temperature = null,
    int? MaxTokens = null, bool UseCache = true, string? Agent = null, string? UserId = null);

public sealed record AiChatResult(
    string Text, string Provider, string? Model, int? TokensUsed = null,
    decimal? CostUsd = null, bool Cached = false);

public sealed record AiTranscriptionRequest(
    byte[] Audio, string? FileName = null, string? Language = null,
    string? Agent = null, string? UserId = null);

public sealed record AiTranscriptionResult(
    string Text, double Duration, string? Language, string Provider,
    string? Model, decimal? CostUsd = null);

public sealed record AiDrugCheckRequest(
    IReadOnlyList<string> Drugs, IReadOnlyList<string>? PatientAllergies = null,
    IReadOnlyList<string>? PatientConditions = null, string? Agent = null, string? UserId = null);

public sealed record AiDrugCheckResult(string Raw, string Provider, bool Cached = false);

public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
```

**Throwing is normal** — the Node gateway throws `"AI Gateway: All providers failed. Last error: …"`
when every provider fails, and each endpoint's degraded response is part of *its* contract, not the
gateway's. The unconfigured placeholder (`UnconfiguredAiGateway`) is what is registered today, so
build the degraded path first.

Per endpoint:

**`POST /check-interactions`** → `CheckDrugsAsync`. Node's parameters are
`temperature 0.1, maxTokens 2000, useCache: false, agent 'drug_check'`, but note the shape:
`AiDrugCheckResult.Raw` is the gateway's own JSON, passed through verbatim, and the endpoint's
`message` **and** `interactions` come out of it — not out of the handler.

Two `message` literals live on that path, both from `aiGateway.js:732-736`:

* `` $"{n} interaction(s) found" `` when `n > 0` — literal parenthesised `(s)`, never pluralized.
* `"No known interactions detected"` when `n == 0` — **and this is the common case.**

`interactions` on the happy path is the **raw LLM array**, echoed with no per-element
normalization: elements may omit keys, carry extras, or hold non-string values. The deterministic
allergy warnings are **prepended** (`[...allergyWarnings, ...enrichedInteractions]`) and always
carry `severity: "HIGH"`, `drug2: "ALLERGY: {allergen}"` and
`recommendation: "DO NOT prescribe. Consider alternatives."`. **Deserialize `interactions` as
loose JSON (`JsonNode`/`JsonElement`), never a typed record.** The client reads
`drug1/drug2/severity/description/recommendation`.

`AiDrugCheckRequest.Drugs` is `IReadOnlyList<string>` — but Node's `allDrugs` can legally contain
non-string **objects**, via `.map(m => m.drugName || m)` at prescriptions.js:780. Two consequences:
`drugsChecked` in the response is `(string|object)[]` and must be emitted from the loose JSON you
built, not from a `List<string>`; and when allergies are non-empty the gateway calls
`.toLowerCase()` on such an object and Node answers
`500 {"error":"Failed to check interactions"}`.

**`POST /extract-from-plan`** → `ChatAsync` (`temperature 0.1, maxTokens 1000, useCache: false,
agent 'specialty_extract'`). Extract the array from `AiChatResult.Text` with the **greedy** regex
`\[[\s\S]*\]` — first `[` to last `]` in the whole response, no markdown-fence stripping, no
JSON-aware parser. Parse failure, non-array, and no-match all yield `200 { "medications": [] }`
with **no error key**.

**`POST /transcribe-rx`** → `TranscribeAsync` then `ChatAsync`
(`temperature 0.1, maxTokens 1000, useCache: true (the DEFAULT — different from the other two),
agent 'scribe'`). `AiTranscriptionResult.Duration` is **seconds**, and becomes the response's
`transcribeDuration` (not `duration`). Here the fence stripping is
``.replace(/```json\n?/g,'').replace(/```\n?/g,'').trim()`` followed by `JSON.parse` of the whole
cleaned string — **different from extract-from-plan**, and both must be ported literally.

Both AI extractors normalize to `RxMedicationLine` (§10): filter on a truthy `drugName`, then
`String(x || '').trim()` into exactly five keys in fixed order.

---

## 6. Caller context, pagination and committing

### `Tebrazi.SharedKernel.Abstractions.ICurrentUser`

```csharp
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    string? Role { get; }
    string? UserType { get; }
    string? OrganizationId { get; }
}
```

House rule, matching Visits and Appointments: **READ handlers inject `ICurrentUser`; WRITE handlers
take the caller id explicitly** on the command (`CallerUserId`) and inject nothing. Groups B and C
are write groups. Group D is mixed — `check-interactions` writes an `InteractionAlert`, so treat
all three as write-shaped and pass the caller id (and, for `check-interactions`, the
`UserType`) explicitly.

`UserType` is a **`string?`**. Compare it as `request.UserType == "PHYSICIAN"`; do **not** parse it
into the `UserType` enum — the claim can carry a value the enum does not have, and two endpoints
branch on it:

* `GET /` — the branch key is `userType`, **not** the presence of a physician profile. A
  RECEPTIONIST or STAFF caller takes the patient branch and in practice sees `[]`.
* `POST /check-interactions` — with no `visitId`, auto-discovery runs only when
  `userType !== 'PHYSICIAN'`. Same body, different result by claim.

`IClinicContext` exists in the kernel but **no prescriptions endpoint reads `X-Clinic-Id`.** Do not
inject it.

### `Tebrazi.SharedKernel.Pagination`

```csharp
public readonly record struct PageRequest(int Page, int Limit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public int Skip => (Page - 1) * Limit;
    public static PageRequest Parse(string? page, string? limit);
}

public sealed record PaginationMeta(int Total, int Page, int Limit, int Pages)
{
    public static PaginationMeta From(int total, PageRequest request);
    public static PaginationMeta Empty(PageRequest request);
}
```

**No prescriptions endpoint uses either type.** `GET /api/prescriptions` returns a bare array with
no envelope. Listed here so you do not go looking.

`GET /interaction-history` has a `?limit=` and it is **not this**: its rule is
`parseInt(req.query.limit) || 20` with **no `Math.max`, no cap, and a meaningful negative** value.
Use `RxJs.ParseInt(string?)` (§12) plus `?? 20` on both null and 0, then hand the raw value —
negative included — to `ListForPhysicianAsync`. `PageRequest.Parse` would clamp it to 1 and break
four distinct inputs.

### `Tebrazi.SharedKernel.Abstractions.Persistence.IDbContext` — how a handler commits

```csharp
Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

Task ExecuteInTransactionAsync(
    Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

Task<TResult> ExecuteInTransactionAsync<TResult>(
    Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default);

Task BeginTransactionAsync(CancellationToken cancellationToken = default);
Task CommitTransactionAsync(CancellationToken cancellationToken = default);
Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
```

Inject `IPrescriptionsDbContext`, never the concrete context and never bare `IDbContext`.
`SaveChangesAsync` also applies the audit stamps (`CreatedAt`/`CreatedBy`, and
`UpdatedAt`/`UpdatedBy` for entities EF sees as **Modified** — which is why `MarkModified` exists).

**Nothing in `prescriptions.js` uses a transaction**, except the per-item `$transaction` inside the
dispense loop, which lives behind `IInventoryDeductionWriter`. So no handler here needs
`ExecuteInTransactionAsync`; a single `SaveChangesAsync` per endpoint is the faithful port. If you
do use it, remember the delegate can **run more than once** after a transient fault, and that the
notification, reminder and inventory ports all commit through *another* module's unit of work and
are therefore **not** covered — call them after it returns, which is also the Node ordering.

Never touch the Begin/Commit/Rollback trio.

---

## 7. Exception → status → body

`APIs/Tebrazi.Common.Api/Middlewares/ExceptionHandlingMiddleware.cs`. `AppException.Error` becomes
the JSON `error` key and `Message` the `message` key — and **`message` is DROPPED when it equals
`error`**, which is what lets a bare `{ error }` body be produced.

| Throw | Status | JSON body |
|---|---|---|
| `new NotFoundException(m)` | 404 | `{"error": m}` |
| `new ForbiddenException(m)` | 403 | `{"error": m}` |
| `new GoneException(m)` | 410 | `{"error": m}` |
| `new UnauthorizedException(m = "Invalid credentials")` | 401 | `{"error":"Unauthorized","message": m}` |
| `new ValidationException(m)` | 400 | `{"error":"Validation failed","message": m}` |
| `new ValidationException(m, details)` | 400 | + `"details": {…}` |
| `new ConflictException(m)` | 409 | `{"error":"Conflict","message": m}` |
| `new BusinessException(e, m, code = 400)` | `code` | `{"error": e}` when `e == m`, else `{"error": e,"message": m}` |
| anything else | 500 | `{"error":"Internal Server Error","message":"An unexpected error occurred"}` (+ `message`/`stack` in Development) |

### The rule for Prescriptions

**Every error body in `prescriptions.js` is a bare `{ "error": "…" }`.** There is no `message` key
anywhere in the file and no `details`. So use `RxErrors` (§12), and **never** `ValidationException`
or `ConflictException` — both prepend a generic label and push the real text into a second key the
client does not match on.

```csharp
throw RxErrors.NotFound("Prescription not found");                        // 404 {"error":"Prescription not found"}
throw RxErrors.Forbidden("Not your prescription");                        // 403 {"error":"Not your prescription"}
throw RxErrors.BadRequest("medicationIndex is required");                 // 400 {"error":"medicationIndex is required"}
throw RxErrors.ServerError("Failed to sign prescription");                // 500 {"error":"Failed to sign prescription"}
throw RxErrors.Node("Physician profile required", 400);                   // any status, bare body
```

**Every route has its OWN named 500 literal**, and the generic middleware body would not reproduce
any of them. Follow the Appointments precedent: one per-file catch-all guard plus a
`Handle` → `HandleCore` split, rethrowing `AppException` **untouched** so your deliberate
400/403/404s keep their bodies. Model it on
`Modules/Appointments/Tebrazi.Appointments.Application/UseCases/AppointmentStatusUseCases.cs`
(`StatusPersistence.RunAsync`), which is:

```csharp
try { return await body(); }
catch (Exception exception)
    when (exception is not AppException && !cancellationToken.IsCancellationRequested)
{
    logger.Error(logMessage, exception);
    throw RxErrors.ServerError(failureError);
}
```

Declare that guard **inside your own file** with your group's prefix (`RxReadPersistence`,
`RxLifecyclePersistence`, …) — `RxErrors` is shared, the guard is not, because the log message and
failure literal differ per route.

The 17 named 500s, so nobody has to re-derive them:

| Endpoint | 500 literal |
|---|---|
| `GET /` | `Failed to list prescriptions` |
| `GET /summary` | `Failed to get prescription summary` |
| `GET /interaction-history` | `Failed to load interaction history` |
| `GET /{id}` | `Failed to get prescription` |
| `GET /{id}/pdf` | `Failed to generate PDF` |
| `POST /` | `Failed to create prescription` |
| `PUT /{id}` | `Failed to update prescription` |
| `PUT /{id}/sign` | `Failed to sign prescription` |
| `PUT /{id}/send` | `Failed to send prescription` |
| `PUT /{id}/dispense` | `Failed to dispense prescription` |
| `POST /{id}/refill-request` | `Failed to request refill` |
| `PUT /{id}/refill-respond` | `Failed to respond to refill` |
| `PUT /{id}/stop-medication` | `Failed to stop medication` |
| `PUT /{id}/resume-medication` | `Failed to resume medication` |
| `POST /check-interactions` | `Failed to check interactions` |
| `POST /extract-from-plan` | `Failed to extract medications` |
| `POST /transcribe-rx` | **the raw `error.message`**, falling back to `Failed to transcribe prescription` when it is empty — the file's ONLY dynamic 500 |

**`/send`'s and `/dispense`'s guards must not swallow the wrong thing.** Node reproduces its
best-effort blocks as *nested* try/catch: `/send`'s notification block and reminder block each have
their own, so a failure in either still answers 200. A read inside a side-effect block must never
turn a committed 200 into a 500 — this was the major finding in Appointments. `/dispense` is the
opposite: its inventory loop has **no** inner catch, so a failure there really is the route's 500.

Model-binding failures never reach a handler: `Program.cs` renders them as
`400 {"error":"Validation failed","message":<first error>}` — a body no prescriptions route ever
produces. **Bind every body as a nullable record with `JsonElement` members** so an absent, null or
wrongly-typed field reaches your own guard rather than the binder's. In particular, a
`int medicationIndex` binding would return that wrong 400 for the four coercion inputs the
stop/resume matrix depends on (§11).

---

## 8. Enums

`Tebrazi.Prescriptions.Domain.Entities`:

```csharp
public enum PrescriptionStatus { DRAFT, SIGNED, CONFIRMED, SENT, DISPENSED }
```
Persists and serializes **as a string** (`HasConversion<string>()`, max length 20). Reachability
through the ported API: **DRAFT is unreachable** (creation auto-signs); `SIGNED` is only ever the
creation state; `CONFIRMED` is what `/sign` writes; `SENT` and `DISPENSED` are their routes'.
There is no state machine — every transition is unconditional and regressions are legal.

`Tebrazi.SharedKernel.Enums` (`Kernel/Tebrazi.SharedKernel/Enums/CoreEnums.cs`):

```csharp
public enum UserRole           { ADMIN, MANAGER, USER, VIEWER }
public enum UserType           { PHYSICIAN, PATIENT, RECEPTIONIST, STAFF }
public enum OrgRole            { OWNER, ADMIN, MEMBER, VIEWER }
public enum SubscriptionPlan   { FREE, STARTER, PRO, ENTERPRISE }
public enum SubscriptionStatus { ACTIVE, PAST_DUE, CANCELED, TRIALING }
public enum Gender             { MALE, FEMALE, OTHER }
public enum DocumentType       { PDF, IMAGE, VIDEO, AUDIO, SPREADSHEET, DOCUMENT, OTHER }
```
Prescriptions touches **none of these as a type.** `ICurrentUser.UserType` is a `string?` — see §6.

Strings that are **not** enums and must stay strings on the wire:

* `Prescription.RefillStatus` — `PENDING | APPROVED | DENIED`, nullable, unvalidated free text.
* `SubprofileSummary.Relation` — `SELF | SPOUSE | CHILD | PARENT | SIBLING | OTHER`.
* `VisitSummary.Status` — `IN_PROGRESS | COMPLETED | CANCELLED | ARCHIVED`.
* `NotificationRequest.Type` — see the four literals in §3.6.
* `check-interactions`' `severity` — contractually `HIGH | MODERATE | LOW` but **not enforced**;
  it comes straight out of the model.
* `MedicationReminderRow.Type` / `.Recurrence` — `"MEDICATION"` / `"DAILY"`.

---

## 9. `using` list for a new use-case file

Place handlers in `Modules/Prescriptions/Tebrazi.Prescriptions.Application/UseCases/`, namespace
`Tebrazi.Prescriptions.Application.UseCases`.

```csharp
using System.Text.Json;                                              // JsonElement bodies, JsonSerializer
using System.Text.Json.Nodes;                                        // JsonNode/JsonArray pass-through (medications, interactions)
using Tebrazi.Prescriptions.Application.Abstractions.Persistence;    // IPrescriptionsDbContext, IPrescriptionStore,
                                                                     // IInteractionAlertStore, PrescriptionFilter
using Tebrazi.Prescriptions.Application.ApiModels.Responses;         // RxMedicationLine (+ your own DTOs)
using Tebrazi.Prescriptions.Application.Services;                    // RxJs, RxErrors, RxDocumentRenderer, RxDocument,
                                                                     // RxMedication, MedicationSchedule, MedicationFrequency
using Tebrazi.Prescriptions.Domain.Entities;                         // Prescription, InteractionAlert, PrescriptionStatus
using Tebrazi.SharedKernel.Abstractions;                             // ICurrentUser, INotificationPublisher, NotificationRequest,
                                                                     // IAiGateway + Ai* records, IMedicationReminderScheduler +
                                                                     // MedicationReminder* records, IInventoryDeductionWriter +
                                                                     // InventoryDeduction* records
using Tebrazi.SharedKernel.Abstractions.Directory;                   // IIdentityDirectory, IVisitDirectory, IClinicDirectory,
                                                                     // IPatientDirectory, IClinicPatientDirectory + their records
using Tebrazi.SharedKernel.Exceptions;                               // BusinessException, NotFoundException, ForbiddenException
using Tebrazi.SharedKernel.Logging;                                  // IAppLogger<T>
using Tebrazi.SharedKernel.Mediation;                                // IRequest<TResult>, IRequestHandler<TRequest,TResult>
```

Optional, add only when used: `using System.Globalization;` (explicit-culture `ToString`/`Parse`),
`using System.Text.Encodings.Web;` (a local `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
serializer), `using System.Text.RegularExpressions;` (the two AI extractors' regexes).
**Not needed by anything here:** `Tebrazi.SharedKernel.Pagination`,
`Tebrazi.SharedKernel.Authorization`, `Tebrazi.SharedKernel.Abstractions.IClinicContext`.

`ImplicitUsings` is enabled, so `System`, `System.Linq`, `System.Collections.Generic` and
`System.Threading.Tasks` are already in scope. `Tebrazi.SharedKernel` arrives transitively through
`Tebrazi.Prescriptions.Domain` — **no csproj edit is needed**, and none is permitted in this phase.

**Never add a MediatR reference.** `using MediatR;` would compile and resolve a *different*
`IMediator`, which reads as correct and is not.

### Shape of a declaration

```csharp
public sealed record XxxCommand(/* … */) : IRequest<XxxResponse>;

public sealed class XxxHandler(/* injected ports */) : IRequestHandler<XxxCommand, XxxResponse>
{
    public async Task<XxxResponse> Handle(
        XxxCommand request, CancellationToken cancellationToken = default) { /* … */ }
}
```
One request record immediately above its handler, both in the same file.
`AddHandlersFromAssembly(typeof(IPrescriptionsDbContext).Assembly)` in `PrescriptionsModule`
discovers them automatically — **no DI edit is needed for a new handler.**

Response type per endpoint. When two variants of one endpoint have incompatible key sets — which
happens twice here, on `POST /check-interactions` and `POST /transcribe-rx` — the Visits precedent
is `IRequest<object>` and returning `(object)` rows. Do **not** emit all keys with nulls:
`transcribe-rx` carries `transcribeDuration` on success and `parseError` on failure, never both.

### Route order (audit-1's six hazards)

The controller must register, in this order: the single-segment literals
`GET /summary`, `GET /interaction-history`, `POST /check-interactions`, `POST /transcribe-rx`,
`POST /extract-from-plan` **before** any parameterised sibling. `GET /summary` and
`GET /interaction-history` before `GET /{id}` is a real dependency in the source, which even
carries the comment *"(must be before /:id)"*. And **do not introduce a `/{id}/{action}` catch-all
or a `POST /{id}`** — neither exists in Node, and either would swallow the literals.

---

## 10. Name collisions across the four files

You are all writing into **one namespace**, `Tebrazi.Prescriptions.Application.UseCases`. Four
files each declaring `RxErrors` or `MessageResponse` is a compile error that none of you can see.

**Prefix every type you declare with your group's prefix:**

| Group | Prefix | Example declarations |
|---|---|---|
| A — read | `RxRead*` | `RxReadListQuery`, `RxReadListResponse`, `RxReadSummaryResponse`, `RxReadGuard`, `RxReadPersistence` |
| B — lifecycle | `RxLifecycle*` | `RxLifecycleCreateCommand`, `RxLifecycleSignResponse`, `RxLifecyclePersistence` |
| C — refill & medication | `RxRefill*` | `RxRefillRequestCommand`, `RxRefillStopResponse`, `RxRefillPersistence` |
| D — AI | `RxAi*` | `RxAiCheckInteractionsCommand`, `RxAiTranscribeResponse`, `RxAiPersistence` |

That applies to `internal` helper classes too — an `internal static class` in a shared namespace
collides exactly as a public one does.

### Types that already exist — do NOT declare these

From `Tebrazi.Prescriptions.Application.ApiModels.Responses`:

* `RxMedicationLine` — the five-key AI medication element
  (`DrugName, Dosage, Frequency, Duration, Instructions`, all non-null trimmed strings, in that
  wire order). Shared by `extract-from-plan` and `transcribe-rx` because the two bodies are
  byte-identical and the key order is the contract. **Group D uses this; nobody redeclares it.**

From `Tebrazi.Prescriptions.Application.Services`:

* `RxJs`, `RxErrors` — §12.
* `RxDocumentRenderer`, `RxDocument`, `RxMedication` — §4.
* `MedicationSchedule`, `MedicationFrequency`, `MedicationReminderNotes` — §12.

From `Tebrazi.Prescriptions.Application.Abstractions.Persistence`:
`IPrescriptionsDbContext`, `IPrescriptionStore`, `IInteractionAlertStore`, `PrescriptionFilter`.

From `Tebrazi.Prescriptions.Domain.Entities`:
`Prescription`, `InteractionAlert`, `PrescriptionStatus`.

From the kernel: everything in §3 and §5-6.

**There is no shared `MessageResponse`, and there should not be one.** Nothing in
`prescriptions.js` answers a bare `{message}`: `/sign`, `/send` and `/dispense` spread the whole
row *and then* append `message`, and stop/resume return `{message, medications}`. Those shapes are
all group-internal, so declare them in your own file with your own prefix.

**There is no shared prescription-row DTO either**, by decision. The 16 scalars appear in six
responses and **no two of them are byte-identical**: `GET /` nests three relations plus a
conditional `patientName`; `POST /` nests a two-key `visit`; `PUT /{id}` is bare; `/sign` and
`/send` append `message`; `/dispense` appends `message` **then** `inventoryDeducted`. A shared base
would force one of those to be wrong. Give each endpoint its own record with the keys in Prisma
model order (`id, visitId, physicianId, subprofileId, status, medications, notes, signedAt,
sentToPatientAt, pdfUrl, refillRequestedAt, refillStatus, refillNotes, createdAt, updatedAt,
deletedAt`) and the appended keys last.

---

## 11. Gaps and decisions

### The two new no-op ports, and exactly what they do not do

`IMedicationReminderScheduler` (§3.7): writes nothing. A patient whose prescription was sent gets
**no medication reminders**, and their existing ACTIVE ones are **not purged**. The `/send`
response is byte-identical to Node's regardless, because it carries nothing about reminders — and
returning 0 correctly suppresses the `MEDICATION_REMINDER_SET` notification, so the patient's inbox
stays honest rather than announcing reminders that do not exist. Best-effort; never 500 over it.

`IInventoryDeductionWriter` (§3.8): decrements nothing and records no transaction history. Returns
`[]`, which the response renders as `"inventoryDeducted": []` — a value Node genuinely produces,
and the value the live client already sees, because `PrescriptionsPage.jsx` sends **no body at
all**. So for the real client this placeholder is behaviourally complete. What is lost: a caller
that *does* send items gets `[]` with no hint nothing moved, and the NaN-quantity 500 becomes a
200. Load-bearing in principle — `null` from a real implementation must become the route's 500.

Neither placeholder pretends the effect happened; both log at warning level naming what they
skipped and why.

### Where the medication-schedule computation landed, and why

**On the Prescriptions side of the port**, in
`Modules/Prescriptions/Tebrazi.Prescriptions.Application/Services/MedicationSchedule.cs` — a pure
static class, same shape as `SpecialtyTemplates` in Visits. Three reasons:

1. It is pure logic over the **prescription's own** free-text `frequency` and `dosage` strings.
   Nothing in `parseFrequency` or `getDefaultDoseTimes` touches `reminders`.
2. Every output is a literal that ends up byte-visible in the rows `/send` writes — the `💊 ` title,
   the `" (Dose 1/2)"` label, the em-dash description, the six-key `notes` JSON. Behind the port a
   reviewer could not diff them against the Node handler.
3. **The dose count is the caller's own contract.** `/send` raises
   `MEDICATION_REMINDER_SET` only when `remindersCreated > 0`, and that notification is
   Prescriptions' side effect, published through `INotificationPublisher`. A port that computed the
   schedule internally would leave the handler unable to reproduce its own notification.

So the port receives **fully formed rows** and only writes them.

### `aiUsageCheck` is not ported

`server/src/middleware/aiUsageLimiter.js` is not in the port, so **`POST /transcribe-rx` has no
quota gate**. Three Node responses therefore do not exist in the .NET backend:

* `403 {"error":"Feature not available on your plan","message":"The Medical Scribe feature requires a Pro subscription.","upgradeRequired":true,"currentTier":"…"}`
* `429 {"error":"Monthly AI limit reached","message":"…","upgradeRequired":true,"currentTier":"…","usage":{…}}`
* `401 {"error":"Authentication required"}` / `401 {"error":"User not found"}` — note these are
  *different bodies* from `authCheck`'s.

Consequence a writer must know: in Node the middleware order is
`multer → aiUsageCheck → handler`, so **a PATIENT calling transcribe-rx gets the plan 403, not
`403 {"error":"Only physicians can dictate prescriptions"}`**. Without the gate, the .NET backend
returns the physician message for that caller. **Do not invent the gate**, and do not call
`IIdentityDirectory.GetSubscriptionTierAsync` to fake it. Record it; it is a known parity gap.
The other two AI endpoints (`check-interactions`, `extract-from-plan`) are unmetered in Node too,
so they are unaffected.

### `cacheMiddleware` is deliberately not reproduced

`GET /` is wrapped in `cacheMiddleware(15)` and `GET /summary` in `cacheMiddleware(30)`, keyed
`route:{userId}:{req.originalUrl}` in a plain in-process Map — and **there is zero invalidation
anywhere in `prescriptions.js`**. So in Node, after a create, sign, send or dispense, the client's
own refresh can legitimately return the pre-mutation array for up to 15s and the pre-mutation
counts for up to 30s.

**Decision already taken: do not reproduce the stale window.** A fresher response cannot break the
client; a stale one can. Put a comment saying so in each of the two handlers (Group A), because a
future reader comparing against Node will otherwise think the caching was missed.

Two knock-on facts that vanish with the cache and are fine to lose: `?visitId=x` was a separate
cache entry from the bare URL, and so were `GET /` and `GET /?`.

### Node's best-effort blocks must stay best-effort

`/send` has **two** independent try/catch blocks (notification, then reminders) and `/dispense`,
refill-request and refill-respond one each. Every read *inside* one of those blocks — the visit
lookup, the physician lookup, the sending doctor's display name — is covered by it in Node, so a
failure there answers **200**, not 500. Reproduce them as nested try/catch inside your file guard.
`/send` queries the visit **twice** (once per block) for the same `patientUserId`; that is
unobservable, so resolving it once is fine, but the failure semantics are not — a single read that
throws must not fail the request.

### `POST /` and `PUT /{id}`: status codes that look wrong

`POST /`: missing physician profile is **400** `{"error":"Physician profile required"}`, while a
foreign or nonexistent visit is **403** `{"error":"Visit not found or not yours"}` — never 404.
Node resolves the visit with a single `findFirst({ id, physicianId })`, so both cases collapse into
the 403. Copy both exactly.

`PUT /{id}`: **the file's only status precondition.** `if (prescription.status === 'SENT')` →
`400 {"error":"Cannot edit a prescription that has already been sent"}`. DISPENSED and CONFIRMED
are still editable; only SENT is blocked. **404 precedes 403.** And an empty body `{}` is a
successful 200 that still bumps `updatedAt` — call `MarkModified` (§1).

`medications: null` on that route is a **500**, not a clear: Prisma refuses a bare null on a
required Json column. Detect `JsonValueKind.Null` and throw
`RxErrors.ServerError("Failed to update prescription")`; do **not** persist the text `"null"`.

### `patientName` on `GET /` must be an ABSENT key, not null

`patientName: patientMap[...] || undefined`, and `JSON.stringify` **drops** `undefined`. So when
the patient's `User` row is missing the key is completely absent from the object — not `null`, not
`""`. The host sets `DefaultIgnoreCondition = JsonIgnoreCondition.Never`, so you must annotate the
property `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, exactly as
`SpecialtyTemplateSection.Paired` does in Visits. And the key exists **only on the physician
branch** — the patient branch response has no `patientName` at all, which is a second reason the
two branches need two DTOs.

### `GET /{id}`'s `isPhysician` serializes as `null`, not `false`

`const isPhysician = physician && prescription.physicianId === physician.id` is the **truthy AND
result**, so for a caller with no physician profile it is `null` (the value of `physician`), and
that is what reaches the client. Model it as `bool?` and set it to `null` — never `false` — when
the profile lookup came back empty. Note the guard is `if (!isPhysician && !isPatient) → 403
{"error":"Access denied"}`, so a `null` here still gates correctly.

### Three different projections of the same relations

`visit`, `physician` and `subprofile` are included with **different selects** on the three GET
endpoints. Do not share a nested DTO between them:

| | `GET /` | `GET /{id}` | `GET /{id}/pdf` |
|---|---|---|---|
| `visit` | `id, chiefComplaint, visitDate, patientUserId, clinic{name}` | + `clinic{name, phone, address}` | `patientUserId, clinicPatientId, clinicPatient{name}, clinic{name, address, city, phone, logo}` |
| `physician` | `specialty, user{displayName}` | + `licenseNumber, user{displayName, email}` | `specialty, licenseNumber, user{displayName}` |
| `subprofile` | `name, relation` | + `dateOfBirth` | `name, relation` |

`POST /` is a fourth: `visit` there is **only** `{chiefComplaint, visitDate}` — no `id` — and there
is no `physician` or `subprofile` object at all.

### The stop/resume coercion matrix — the trap that a typed binder loses

Bind `medicationIndex` as a `JsonElement`, and reproduce this table exactly. A
`int medicationIndex` model binding returns the framework's `400 {"error":"Validation failed",…}`
for every row below and is wrong four times over.

| Input | `stop-medication` | `resume-medication` |
|---|---|---|
| absent / `null` | `400 {"error":"medicationIndex is required"}` | **500** `{"error":"Failed to resume medication"}` — there is no required check; `undefined` passes both bounds tests and destructuring `meds[undefined]` throws |
| `null` when `meds` is `[]` | `400 medicationIndex is required` | `400 {"error":"Invalid medication index"}` — `null >= 0` is **true** |
| `0` | valid (passes the required check) | valid |
| `"1"` (numeric string) | valid — JS coerces the index | valid |
| `"abc"` / `NaN` / `1.5` | **200** `{"message":"Stopped undefined","medications":<UNCHANGED>}` — the write is a no-op because `JSON.stringify` drops non-index array properties | **500** `Failed to resume medication` |
| valid index, element has no `drugName` | `200 {"message":"Stopped undefined", …}` | `200 {"message":"Resumed undefined", …}` |
| out of range | `400 Invalid medication index` | `400 Invalid medication index` |

Also: both routes echo the **in-memory array they just built**, not the DB row, with untouched
elements verbatim in original key order. `stoppedAt` is a **string** (`new Date().toISOString()`),
not a DateTime — keep the milliseconds and the `Z`. `stoppedByPatient` is the literal `true`.
Resume **removes** both keys (absent, not null). And audit-1 corrects the chunk file here:
`resume-medication` **can never** return `medications: []` on a 200 — with a non-array stored
value every index either 400s or 500s.

### The em dash, and the other exact characters

`POST /check-interactions`' one-drug message is
`Only 1 active medication found — no interactions to check` with **U+2014 surrounded by single
spaces**. The zero-drug message is `No active medications found` (no dash). The host serializes
with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, so a hyphen substituted for the em dash is a
byte-level divergence. The same character appears in the reminder description separator
(`"{dosage} — Take at {time}"`) and in `RxDocumentRenderer`'s `|| '—'` cell fallback, and the `💊`,
`🖨` and `℞` glyphs must survive as UTF-8.

### `check-interactions` has no validation and no authorization at all

No 400, no 403 anywhere on the route: `visitId` and `subprofileId` are used **unvalidated and
unchecked**, so any authenticated caller can pass another patient's `visitId` and receive that
patient's full active drug list back in `drugsChecked`. It is an IDOR, the response contract
depends on it, and a faithful port reproduces it. **Flag it to the team; do not silently fix it.**

Discovery order decides `drugsChecked`, which is observable: caller `medications` first, then the
subprofile's `CurrentMedication`, then cross-prescription medications (prescription order, then
array order), then all family subprofiles' medications. Dedup is
`[...new Set([...inputMeds, ...existingDrugs].filter(Boolean))]` — **exact-string and
case-SENSITIVE**, so `"Aspirin"` and `"aspirin"` both survive. A `HashSet<string>` loses the
insertion order; use a `List` plus a seen-set, and remember the elements may not be strings.

### `POST /extract-from-plan`'s 400 gate is a length test

`if (!planText || planText.trim().length < 5)` → `400 {"error":"Plan text is required"}`. So
`"abc"`, `"ok"`, `"    "` and `"1234"` all 400, not just empty input. A **non-string** `planText`
reaches `.trim()` and throws → `500 {"error":"Failed to extract medications"}`, not 400. And AI
parse failures are invisible: `200 { "medications": [] }` with **no error key**.

### `GET /`, `GET /summary` and `GET /interaction-history` answer patients with a shape, not a 403

A caller with no physician profile gets `200 []`, `200 {"signed":0,"sent":0,"total":0}` and
`200 []` respectively, via early returns that never issue the counting queries
(prescriptions.js:41, :148, :171). **A port that gates these on a physician role returns 403 and
breaks the patient pages.** For `/summary` this is the only early return, and it emits literal
zeros, not computed ones.

### Other facts a writer cannot see from the code

* **`POST /` is the only route with `auditLog`**, and the audit row is fire-and-forget with its
  failure swallowed. `activity_logs` is not ported, so the row is simply not written; nothing in
  the response changes.
* **The global XSS sanitizer** (`server/src/index.js:57-81`) rewrites every string in `req.body`
  before every JSON-body handler here: it strips HTML tags and `on*=` handlers, deletes
  `javascript:` / `data:` / `vbscript:`, strips `expression(` and rewrites `url(` to `url\(`. So
  stored drug names, `notes`, `refillNotes` and the `planText` sent to the model are the
  *sanitized* strings. It is **not ported**, so stored values and AI prompts differ from Node's for
  inputs containing those substrings. Known gap; do not implement it inside a handler.
* It does **not** touch `transcribe-rx`, because it runs before multer populates `req.body` — so
  `language` reaches the Node handler unsanitized too.
* **`GET /{id}/pdf` fetches the prescription BEFORE the authorization check.** Keep that order if
  you port it as guarded statements; it is a timing side channel only, but it is the order.
* **`/dispense` writes DISPENSED before touching inventory**, with no wrapping transaction. Keep
  that order — it is what makes the 500-with-a-dispensed-prescription outcome reachable, and that
  outcome is the contract.
* **Every re-`/send` re-runs the whole side-effect chain**: another notification, another email,
  another push, another purge, another full set of reminder rows. No idempotency key, no
  `sentToPatientAt` check.
* `POST /` **ignores** client-supplied `subprofileId`, `status` and `signedAt`.
* `PUT /{id}/sign` and `/send` collapse "caller is not a physician at all" and "prescription
  belongs to someone else" into the **same** `403 {"error":"Not your prescription"}`. There is no
  "Physician profile required" message on those routes.

---

## 12. Reusable helpers

### Published in this module — use these, do not redeclare them

`Modules/Prescriptions/Tebrazi.Prescriptions.Application/Services/RxJsSemantics.cs`, namespace
`Tebrazi.Prescriptions.Application.Services`:

```csharp
public static class RxJs
{
    public static string? Truthy(string? value);
    public static bool IsTruthy(JsonElement value);
    public static bool IsTruthy(JsonElement? value);
    public static int? ParseInt(string? value);
    public static int? ParseInt(JsonElement value);
}

public static class RxErrors
{
    public static BusinessException Node(string message, int statusCode);
    public static BusinessException BadRequest(string message);   // 400
    public static ForbiddenException Forbidden(string message);    // 403
    public static NotFoundException NotFound(string message);      // 404
    public static BusinessException ServerError(string message);   // 500
}
```

* `Truthy` — `null` for both null and `""`, so `if (visitId)` ports as
  `RxJs.Truthy(visitId) is { } id`.
* `IsTruthy` — full JS truthiness: `false`, `0`, `""`, `null` and an absent key are falsy;
  **every object and array is truthy, including `{}` and `[]`**.
* `ParseInt(string?)` — JS `parseInt`: digit prefix, so `"50abc"` → 50, `"1e6"` → **1**, `"3.9"` →
  3, `"abc"` → null. For `?limit=`, combine with `?? 20` on **both null and 0**.
* `ParseInt(JsonElement)` — JS `parseInt` over a `number|string` field, for the dispense
  quantities: a number is stringified first (`1.9` → 1), a string takes the prefix rule, and
  bool/object/array/null are null (NaN).

`Modules/Prescriptions/Tebrazi.Prescriptions.Application/Services/MedicationSchedule.cs`:

```csharp
public readonly record struct MedicationFrequency(int DosesPerDay, int IntervalHours);

public static partial class MedicationSchedule
{
    public static MedicationFrequency ParseFrequency(string frequency);
    public static IReadOnlyList<string> GetDefaultDoseTimes(int dosesPerDay);
    public static DateTime NextOccurrence(string doseTime, DateTime? nowLocal = null);
    public static MedicationReminderGroup BuildGroup(
        string drugName, string? dosage, string frequency, DateTime? nowLocal = null);
}

public sealed record MedicationReminderNotes(
    string DrugName, string Dosage, string Frequency,
    int DoseNumber, int TotalDoses, int IntervalHours);
```

`BuildGroup` is the one Group B needs — it returns a ready `MedicationReminderGroup` with every
literal composed. Call it only for a medication with **both** a truthy `drugName` and a truthy
`frequency`.

Behaviour worth knowing before you trust it: `ParseFrequency` clamps to 1-12 and falls through to
1 for unrecognised text (no error path); the regex order is load-bearing;
`"every 0 hours"` yields **12** because JS divides by zero to `Infinity`; and the second branch
tests with one pattern and extracts the number with a **looser** one, so `"5 doses, 3 times a day"`
resolves to **5**. `GetDefaultDoseTimes` returns a **non-ascending** list for 5+ doses (it wraps
past midnight). `NextOccurrence` works in **server-local** time, not the patient's, and rolls a
minute component of 60 into the next hour.

**Pass one `nowLocal` for the whole prescription.** Node re-reads the clock per dose, which is
unobservable, but a single reference instant keeps the rows self-consistent.

`ParseFrequency` takes a `string`. Node calls `.toLowerCase()` unguarded, so a **non-string**
`frequency` throws a TypeError there, which the reminder block's own try/catch swallows and the
whole block aborts: no purge, no rows, no notification #2. Reproduce that by abandoning the
**entire plan** when you meet a non-string frequency — do not coerce it, and do not skip just that
one medication.

### The Appointments / Visits helpers — you CANNOT reach them

Every JS-semantics helper the previous two phases wrote is `internal` to **its own assembly**, so
none of it is visible from `Tebrazi.Prescriptions.Application`:

| Helper | File | Type | Why you cannot use it |
|---|---|---|---|
| `parseFloat(x.toFixed(n))` port — `JsToFixed`, `JsRound`, `Fixed1`, `Fixed3` | `Modules/Appointments/…/UseCases/AppointmentReadUseCases.cs` | `private static` members of the handler class | Not even `internal` — private to one class in another assembly. |
| ECMA `new Date(string)` date-only parser — `TryParseIsoDateOnly`, `IsTimeOnly` | `Modules/Appointments/…/UseCases/AppointmentReadUseCases.cs` | `internal static partial class ApptReadJsDate` | `internal` to `Tebrazi.Appointments.Application`. |
| JS truthiness over `JsonElement` / `JsonNode` | same file, and `SlotUseCases.cs`, `AppointmentAssistantUseCases.cs`, `Visits/…/VisitAiUseCases.cs` | `internal static class ApptReadJsValues` / `SlotJs` / `AssistantJs` / `VisitAiJsValues` | four `internal` copies, all in other assemblies. |
| `Truthy(string?)` | `AppointmentReadUseCases.cs`, `SlotUseCases.cs`, `Visits/…/VisitReadUseCases.cs` | `internal static class ApptReadFilters` / `SlotFilters` / `VisitReadFilters` | same. |
| Node-shaped error factory | `AppointmentStatusUseCases.cs` etc. | `internal static class StatusErrors`, `SlotErrors`, `BookingErrors`, … | same. |

**Decision: re-implement locally, and it is already done for you.** `RxJs` and `RxErrors` above are
the Prescriptions copies of the truthiness, `parseInt` and error-factory helpers, published
(`public`) in this module's shared surface rather than duplicated four times across the parallel
files — which is where Appointments ended up with five copies of `Truthy` and four of `IsTruthy`.
Do **not** lift the Appointments versions into the kernel: that means editing files in another
module in a phase where four agents cannot rebuild, for helpers whose only shared consumer would be
a fifth copy. Recorded here so the next module's surface pass can make the kernel-lift call
deliberately, with all three modules' copies in front of it.

**Neither of the other two is needed here.** No prescriptions endpoint performs numeric rounding —
the only numbers on the wire are `GET /summary`'s three integer counts, `alertCount`, and the
dispense quantities, all of them integers — so there is no reason for a `toFixed` port. And no
endpoint parses a date out of a request: `GET /` has no date filter, `PUT /{id}` writes only
medications and notes, and `/pdf` **formats** a stored `DateTime` rather than parsing one. If you
find yourself wanting either, you have misread the contract — check before writing one.
