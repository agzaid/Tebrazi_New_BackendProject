# Appointments — shared surface inventory

Everything the 19 Appointments handlers may call, transcribed **verbatim** from the code as it
stands after the surface pass. Five handler files are being written in parallel and none of them
can build, so this document is the contract between them: if a signature is not here, it does not
exist.

Built clean at the time of writing: `dotnet build Tebrazi.Backend.sln` → **0 errors**, and 0
warnings from any file in this inventory (the solution's 224 remaining `CS1573` warnings are
pre-existing, all in Visits / Prescriptions / Identity / kernel files untouched by this pass).

Sections:

1. [`IAppointmentStore` / `ITimeSlotStore`](#1-store-surface)
2. [`Appointment` / `TimeSlot`](#2-entity-surface)
3. [Cross-module ports](#3-cross-module-ports)
4. [`ICurrentUser`, `IClinicContext`, `PageRequest`, `IDbContext`](#4-caller-context-pagination-and-committing)
5. [Exception → status code → JSON body](#5-exception--status--body)
6. [Enums](#6-enums)
7. [The `using` list for a new use-case file](#7-using-list-for-a-new-use-case-file)
8. [Gaps and decisions](#8-gaps-and-decisions)

---

## 1. Store surface

`Tebrazi.Appointments.Application.Abstractions.Persistence` —
`Modules/Appointments/Tebrazi.Appointments.Application/Abstractions/Persistence/IAppointmentStores.cs`

### Supporting types

```csharp
public interface IAppointmentsDbContext : IDbContext;

public sealed record AppointmentFilter(
    string? ClinicId = null,
    string? PhysicianId = null,
    string? PatientUserId = null,
    string? ClinicPatientId = null,
    AppointmentStatus? Status = null,
    DateTime? Date = null,
    DateTime? From = null,
    DateTime? To = null);

public sealed record BookedSlot(string StartTime, string EndTime);
```

`AppointmentFilter`: a **null** member is not applied, matching how the Node handler builds its
`where` key by key. `Date` matches the whole calendar day (`>= date.Date && < date.Date.AddDays(1)`).
`From`/`To` are a half-open day range and are **not used by any of the 19 endpoints** — do not
reach for them.

> Pass `null`, not `""`, for an absent filter. ASP.NET hands a present-but-valueless query key over
> as `""`, and every store test is `is not null`, so `?clinicId=` would filter on the empty string
> and return nothing where Node returns everything. Node gates each filter on JS truthiness
> (`if (clinicId)`), so map `""` → `null` in the handler. Visits keeps a one-liner for this
> (`VisitReadFilters.Truthy`); Appointments has none yet — declare an `internal static` helper in
> your own file if you want one.

### `IAppointmentStore`

```csharp
Task<Appointment?> GetForUpdateAsync(string id, CancellationToken ct = default);
```
TRACKED single row by id. No soft-delete filter, no status filter. Use for every endpoint that
mutates (`/confirm`, `/cancel`, `/complete`, `/no-show`, `/check-in`, `DELETE /{id}`).

```csharp
Task<Appointment?> GetAsync(string id, CancellationToken ct = default);
```
Same query, `AsNoTracking()`. Use for `POST /{id}/intake-note`, which reads the appointment but
never writes it.

```csharp
Task<(IReadOnlyList<Appointment> Items, int TotalCount)> PageAsync(
    AppointmentFilter filter, int page, int pageSize, CancellationToken ct = default);
```
Paginated, ordered `AppointmentDate` then `StartTime`, `pageSize` clamped to 1..100.
**No Appointments endpoint paginates.** Do not use it for `GET /` — see `ListAsync`.

```csharp
Task<IReadOnlyList<Appointment>> ListAsync(
    AppointmentFilter filter, int take, CancellationToken ct = default);
```
**NEW in this pass.** The unpaginated list behind `GET /api/appointments`: ordered by
`AppointmentDate` **only** (no second sort key, so same-day ties are database-ordered exactly as in
Node), truncated to `take`. Pass `take: 200` — appointments.js:400-401. Returns `[]` for
`take < 1`. `GET /` answers a naked JSON array with no total and no metadata.

```csharp
Task<IReadOnlyList<Appointment>> ListForDayAsync(
    string? clinicId,
    string? physicianId,
    DateTime date,
    IReadOnlyCollection<AppointmentStatus>? statuses = null,
    CancellationToken ct = default);
```
**`statuses` is NEW in this pass.** One calendar day, ordered by `StartTime` (string sort).
`clinicId`/`physicianId` are each applied only when non-null. `statuses` translates to a
parameterised `IN`; `null` means no status filter (no endpoint wants that), and an **empty
collection means "match nothing"**, not "no filter".

The two day-scoped endpoints pass different sets:

| Endpoint | `statuses` | Node |
|---|---|---|
| `GET /today` | `[PENDING, CONFIRMED]` | appointments.js:525 |
| `GET /queue` | `[PENDING, CONFIRMED, COMPLETED, NO_SHOW]` (everything but `CANCELLED`) | appointments.js:1240 |

```csharp
Task<IReadOnlyList<BookedSlot>> ListBookedAsync(
    string physicianId, DateTime date, CancellationToken ct = default);
```
The start/end times already taken in a physician's day, for `GET /available`. Filter baked in:
`PENDING` and `CONFIRMED` only — **`COMPLETED` does not hold a slot** (appointments.js:314) — and
**no `clinicId` filter**, so a booking at the physician's other clinic marks this clinic's slot
booked. Both are the contract. `EndTime` is returned but Node never uses it: match on `StartTime`
alone.

```csharp
Task<bool> HasBookingAtAsync(
    string physicianId, DateTime date, string startTime, CancellationToken ct = default);
```
The 409 guard on `POST /`. **Exact `StartTime` string equality, not interval overlap**
(appointments.js:450-458), `PENDING`/`CONFIRMED` only, and **not clinic-scoped**. So 09:00-10:00
does not block 09:30-10:00, and clinic A's booking does block clinic B's same start time. It is a
check-then-create with no transaction and no unique index, so two concurrent requests both pass and
both 201 — reproduce that; do not add a lock.

```csharp
Task<IReadOnlyList<Appointment>> ListByRecurringGroupAsync(
    string recurringGroupId, CancellationToken ct = default);
```
Every instance of one series, ordered by `AppointmentDate`. **No endpoint among the 19 calls it** —
`POST /recurring` returns the rows it just created, in creation order.

```csharp
Task<IReadOnlyList<Appointment>> ListForPhysicianSinceAsync(
    string physicianId, DateTime since, CancellationToken ct = default);
```
`GET /ai-optimize`'s 90-day window. **Confirmed correct for it**: `PhysicianId == physicianId &&
AppointmentDate >= since`, ordered by `AppointmentDate` ascending, **no status filter and no clinic
filter** — cancellations and no-shows are exactly what the optimiser analyses (appointments.js:1343-1360).
It returns whole entities where Node `select`s nine columns; that is a superset, so every field the
statistics need is present.

```csharp
void Add(Appointment appointment);
void AddRange(IEnumerable<Appointment> appointments);
void Remove(Appointment appointment);
```
`Remove` is a **HARD delete** — `DELETE /api/appointments/{id}` calls `prisma.appointment.delete`
(appointments.js:948). Never `Appointment.SoftDelete()` for that endpoint.

`POST /recurring` must use `Add` + `SaveChangesAsync` **per occurrence**, not `AddRange` + one save:
Node awaits 1-12 sequential creates with no transaction, so a failure on occurrence *k* leaves *k-1*
rows persisted under a `groupId` the client never learns (audit-2). `AddRange` would leave zero.

### `ITimeSlotStore`

```csharp
Task<TimeSlot?> GetForUpdateAsync(string id, CancellationToken ct = default);
```
TRACKED, no `IsActive` filter. For `DELETE /slots/{id}`, which soft-deletes by calling
`Deactivate()`.

```csharp
Task<IReadOnlyList<TimeSlot>> ListActiveAsync(
    string physicianId, string? clinicId = null, CancellationToken ct = default);
```
`IsActive == true`, ordered `DayOfWeek` then `StartTime`. Backs `GET /slots` (pass the truthy
`?clinicId`) and `GET /ai-optimize`'s `activeSlots` (pass `clinicId: null` — the optimiser counts
slots across every clinic).

```csharp
Task<IReadOnlyList<TimeSlot>> ListActiveForDayOfWeekAsync(
    string physicianId, int dayOfWeek, string? clinicId = null, CancellationToken ct = default);
```
`IsActive == true` for one weekday, ordered by `StartTime`. Backs `GET /available`.
`dayOfWeek` is 0=Sunday..6=Saturday (JavaScript `Date.getDay()`).

```csharp
Task<IReadOnlyList<TimeSlot>> ListAllAsync(
    string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default);
```
TRACKED, **no `IsActive` filter**, unordered. Present for a handler that wants the rows before
deleting them. **Prefer `DeleteAllAsync`** for the two destructive routes.

```csharp
Task<int> DeleteAllAsync(
    string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default);
```
**NEW in this pass.** The `deleteMany` itself: one `ExecuteDeleteAsync` statement that **commits on
its own**, independently of `SaveChangesAsync`, returning the row count. No `IsActive` filter, so
deactivated rows go too. `dayOfWeek` narrows to one weekday for `POST /slots/bulk`
(appointments.js:145); omit it for `POST /slots/sync-from-hours`, which wipes **every** weekday at
the clinic (appointments.js:201-202) — a schedule posting Monday alone erases Tue-Sun.

> **Do not wrap the delete and the create in `ExecuteInTransactionAsync`.** See
> [§8 destructive-then-fail](#destructive-then-fail-is-the-contract).

```csharp
void Add(TimeSlot slot);
void AddRange(IEnumerable<TimeSlot> slots);
void Remove(TimeSlot slot);
void RemoveRange(IEnumerable<TimeSlot> slots);
```

---

## 2. Entity surface

`Tebrazi.Appointments.Domain.Entities`

### `Appointment : MutableEntity<string>`

Constructor is `private`. Build with `Create`, then apply behaviour methods.

Inherited (via `MutableEntity<string>` → `ImmutableEntity<string>` → `Entity<string>`), all
`protected set` so read-only to a handler:

| Member | Type |
|---|---|
| `Id` | `string` |
| `CreatedAt` | `DateTime` |
| `CreatedBy` | `string` (defaults `"UNKNOWN"`) |
| `UpdatedAt` | `DateTime?` |
| `UpdatedBy` | `string?` |

`CreatedAt`/`UpdatedAt` are stamped by `BaseDbContext` on save — never by hand.

Own properties, all `{ get; private set; }`:

| Member | Type | Notes |
|---|---|---|
| `ClinicId` | `string` | |
| `PhysicianId` | `string` | physician **profile** id |
| `PatientUserId` | `string` | no FK in the schema — dangling ids are reachable |
| `SubprofileId` | `string?` | |
| `ClinicPatientId` | `string?` | never written by any of the 19 endpoints |
| `Status` | `AppointmentStatus` | defaults `PENDING` |
| `AppointmentDate` | `DateTime` | |
| `StartTime` | `string` | **"HH:mm", a STRING.** Never convert to `TimeOnly` |
| `EndTime` | `string` | **"HH:mm", a STRING** |
| `Reason` | `string?` | |
| `Notes` | `string?` | carries the `CHECKIN:` markers |
| `AppointmentType` | `AppointmentType` | defaults `IN_PERSON` |
| `ConfirmedAt` | `DateTime?` | |
| `CompletedAt` | `DateTime?` | |
| `CancelledAt` | `DateTime?` | |
| `CancelReason` | `string?` | |
| `RecurringRule` | `string?` | `"WEEKLY"`/`"BIWEEKLY"`/`"MONTHLY"`, free text |
| `RecurringGroupId` | `string?` | |
| `DeletedAt` | `DateTime?` | schema parity only; nothing reads or writes it |

That is 19 own + `Id`, `CreatedAt`, `UpdatedAt` = **the 22 scalars the client sees**
(`createdBy`/`updatedBy` are not Node columns — do NOT serialize them).

```csharp
public const string CheckInMarker = "CHECKIN:";
```
A **parsing contract**, not a detail. `GET /queue` recovers `checkedIn`/`checkedInAt`/`room` by
testing `notes.Contains(CheckInMarker)` and JSON-parsing everything after the **last** occurrence
(appointments.js:1260-1268). Both the check-in writer and the queue reader must use this constant.

```csharp
public static Appointment Create(
    string clinicId,
    string physicianId,
    string patientUserId,
    DateTime appointmentDate,
    string startTime,
    string endTime,
    string? subprofileId = null,
    string? clinicPatientId = null,
    string? reason = null,
    string? notes = null,
    AppointmentType appointmentType = AppointmentType.IN_PERSON,
    AppointmentStatus status = AppointmentStatus.PENDING,
    string? recurringRule = null,
    string? recurringGroupId = null)
```
Assigns a `Guid.NewGuid().ToString()` id and **trims** `startTime`/`endTime`. Throws
`ArgumentException` on a null/blank `clinicId`, `physicianId`, `patientUserId`, `startTime` or
`endTime` — those are *programmer* errors, not the 400s; validate the body and throw
`BusinessException` yourself first.

`POST /walk-in` wants `status: CONFIRMED` **and** `confirmedAt = now`: call `Create(...)` and then
`Confirm()` (appointments.js:597-598).

```csharp
public void Update(
    DateTime? appointmentDate = null,
    string? startTime = null,
    string? endTime = null,
    string? reason = null,
    string? notes = null,
    AppointmentType? appointmentType = null)
```
Partial patch, null leaves a field alone. **No endpoint among the 19 uses it** — there is no
`PUT /api/appointments/{id}` in the Node router.

```csharp
public void Confirm()
```
`PUT /{id}/confirm`. Status → `CONFIRMED`; **`ConfirmedAt` is OVERWRITTEN** (appointments.js:762
writes `confirmedAt: new Date()` unconditionally), so re-confirming moves the timestamp and the
response shows the new one. Also used by `POST /walk-in`.

```csharp
public void Complete()
```
`PUT /{id}/complete`. Status → `COMPLETED`; **`CompletedAt` OVERWRITTEN** (appointments.js:880).

```csharp
public void Cancel(string? reason)
```
`PUT /{id}/cancel`. Status → `CANCELLED`; **`CancelledAt` OVERWRITTEN** (appointments.js:814);
`CancelReason = reason` verbatim, no length limit. Pass `null` for an absent **or empty-string**
body reason — `cancelReason: reason || null` (appointments.js:815) stores `""` as null, so the
response reports null for a reason the client believes it sent.

```csharp
public void MarkNoShow()
```
`PUT /{id}/no-show`. Status **only** — appointments.js:914 writes no timestamp.

```csharp
public void CheckIn(string checkInPayloadJson)
```
`PUT /{id}/check-in` (appointments.js:1006-1013). Three writes at once:
* status → `CONFIRMED`;
* `ConfirmedAt ??= UtcNow` — **PRESERVED** when already set (`appt.confirmedAt || new Date()`), the
  opposite of `Confirm()`;
* appends `"CHECKIN:" + checkInPayloadJson` to `Notes` behind a **single** `\n`, keeping existing
  notes. So a walk-in reads `"WALK_IN\nCHECKIN:{…}"` and a third check-in stacks a third marker; an
  empty/null `Notes` produces no leading newline.

Pass the serialized `{ checkedInAt, checkedInBy, room }` object **without** the marker prefix.
Throws `ArgumentNullException` on null. **Do not use `AppendNote` here** — it separates with a blank
line, which changes the bytes `GET /queue` parses.

```csharp
public void AppendNote(string note)   // separates with "\n\n"; NOT for check-in
public void SetNotes(string? notes)
public void SoftDelete()              // writes DeletedAt; no Node endpoint does this
```
None of these three is used by the 19 endpoints.

### `TimeSlot : ImmutableEntity<string>`

Constructor is `private`. Inherited: `Id` (`string`), `CreatedAt` (`DateTime`), `CreatedBy`
(`string`). **No `UpdatedAt`** — Prisma declares none on this model.

| Member | Type | Notes |
|---|---|---|
| `ClinicId` | `string` | |
| `PhysicianId` | `string` | physician **profile** id |
| `DayOfWeek` | `int` | 0=Sunday..6=Saturday |
| `StartTime` | `string` | "HH:mm" |
| `EndTime` | `string` | "HH:mm" |
| `SlotDuration` | `int` | minutes; defaults 30 |
| `IsActive` | `bool` | defaults true |

```csharp
public static TimeSlot Create(
    string clinicId,
    string physicianId,
    int dayOfWeek,
    string startTime,
    string endTime,
    int slotDuration = 30,
    bool isActive = true)
```
Throws `ArgumentException` on blank strings, `ArgumentOutOfRangeException` when
`dayOfWeek < 0 || > 6` or `slotDuration < 1`.

> **The `slotDuration` defaults differ per route and the entity default cannot cover them.** Node
> uses `s.slotDuration || 30` in `POST /slots/bulk` (appointments.js:148) and
> `slotDuration || 15` in `POST /slots/sync-from-hours` (appointments.js:210). It is `||`, so a sent
> **0 becomes the default**. Resolve the value in the handler and pass it explicitly.
>
> Also: `dayOfWeek` reaches Node as `parseInt(dayOfWeek)` with no range check, so a value outside
> 0-6 reaches Prisma and 500s. `Create` throws `ArgumentOutOfRangeException` instead, which the
> middleware renders as its generic 500 — a different body from Node's
> `{"error":"Failed to create bulk slots"}`. Validate the range yourself and throw
> `BusinessException(<node message>, <same>, 500)` if you want byte parity.

```csharp
public void Deactivate()   // IsActive = false — this IS DELETE /slots/{id}
public void Activate()
```

---

## 3. Cross-module ports

All in assembly `Tebrazi.SharedKernel`, reachable transitively from
`Tebrazi.Appointments.Application`. Nothing here needs a new project reference.

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

* `GetPhysicianByUserIdAsync` is the `prisma.physicianProfile.findUnique({ where: { userId } })`
  every route opens with. `null` means "not a physician" — each endpoint answers that differently
  (`[]`, 400, or 403; see §8).
* `GetPhysicianAsync(profileId)` turns the stored `PhysicianId` into the **user id** a notification
  is addressed to. Use it, not `GetPhysiciansAsync([id])`.
* `GetPhysicianDetailsAsync` is for `GET /queue` **only** — that endpoint serializes the whole
  profile row plus a nested `user: { displayName }`, `ScratchpadNotes` included. `GET /` needs only
  `Specialty` + `DisplayName`, so use `GetPhysiciansAsync` there.
* Read-only. Never resolves a subscription/tier for these endpoints.
* Ids absent from a dictionary result simply did not resolve — treat as null, never as an error.

### 3.2 `Tebrazi.SharedKernel.Abstractions.Directory.IClinicDirectory`

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

* `IsOwningPhysicianAsync` is the **ownership** test (`clinics.physician_id == profileId`) behind
  `403 "Not your clinic"` on the three slot-write routes and behind `GET /queue`'s physician branch.
  It returns false both for "no such clinic" and "someone else's clinic", which is exactly what
  Node's `findFirst({ id, physicianId })` collapses them to. **Not** membership — do not substitute
  `IClinicAccessEvaluator`.
* `IsActiveStaffAsync` is the `clinicStaff.findFirst({ userId, clinicId, isActive: true })` gate.
* `ConsultationFee` / `FollowUpFee`: nullable, entity-defaulted 300/200. The check-in path must
  treat **0 as unset** (`is null or 0`), because Node uses `||`, not `??` — so an explicitly
  configured fee of 0 silently becomes 300/200 and the `fee > 0` test is effectively always true.
* `AllowPatientBooking` gates `GET /available`'s silent `200 []`.
* `ListActiveStaffUserIdsAsync` is a Visits fan-out helper — **no Appointments endpoint needs it**.

### 3.3 `Tebrazi.SharedKernel.Abstractions.Directory.IClinicWorkingHoursWriter`

```csharp
Task<bool> SetWorkingHoursAsync(string clinicId, string? workingHoursJson, CancellationToken ct = default);
```
The only clinic **write** in this module, needed by `POST /slots/sync-from-hours`, which after
generating slots overwrites the clinic's `workingHours` with `JSON.stringify(schedule)` of the
**RAW request body** — including entries it skipped as malformed (appointments.js:240, audit-2).
Pass the value already serialized, so the double encoding stays visible at the call site. Returns
false when no such clinic exists; commits through the **Clinics** unit of work, so it is a second
commit.

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
Appointments needs `Name` + `Relation` only, for the `subprofile: { name, relation }` object on
`GET /`, `GET /today`, `GET /queue` and `POST /`'s 201. `Relation` is a plain string carrying the
`SubprofileRelation` enum (`SELF|SPOUSE|CHILD|PARENT|SIBLING|OTHER`). The clinical-context and
medication methods belong to Visits/Prescriptions — no Appointments endpoint calls them.

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
Available, but **no Appointments endpoint projects a clinic-patient chart** — none of the 19 Node
handlers includes the `clinicPatient` relation. Do not invent one.

### 3.6 `Tebrazi.SharedKernel.Abstractions.Directory.IVisitDirectory`

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
public sealed record VisitQueueRow(
    string Id, string PatientUserId, string? SubprofileId, string? ClinicPatientId,
    string Status, DateTime VisitDate, DateTime? FollowUpDate,
    string? FollowUpNotes, DateTime? CompletedAt);

public sealed record VisitDuration(DateTime VisitDate, DateTime CompletedAt);   // CompletedAt never null

public sealed record VisitSummary(
    string Id, string OrganizationId, string ClinicId, string PhysicianId,
    string PatientUserId, string? SubprofileId, string? ClinicPatientId,
    DateTime VisitDate, string Status, string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis, string? Plan);
```

Three Appointments uses:

* **`CountCompletedAsync`** → `/check-in`'s follow-up detection. `> 0` means follow-up
  (appointments.js:1074-1081).
* **`ListForClinicDayAsync`** → `GET /queue`'s visit overlay. **Applies no status filter**, matching
  appointments.js:1273-1282 — so `Status` can be `IN_PROGRESS`, `COMPLETED`, `CANCELLED` **or
  `ARCHIVED`**. The route's own comment at appointments.js:1294 claims two values and is wrong
  (audit-2, `breaks-client`). Keep it a **string**; do not narrow it to an enum. Field names here are
  the model's — the response renames them (`FollowUpDate` → `visitFollowUp`, `FollowUpNotes` →
  `visitFollowUpNotes`, `CompletedAt` → `visitCompletedAt`), and that renaming belongs in the DTO.
  Node builds its overlay map keyed by `patientUserId` **with no `orderBy`**, so when a patient has
  several visits today the last row returned wins non-deterministically — do not "fix" it by picking
  the newest.
* **`ListCompletedDurationsAsync`** → `GET /ai-optimize`'s `avgVisitDurationMin`. Only visits with a
  non-null `completed_at` come back.

### 3.7 `Tebrazi.SharedKernel.Abstractions.INotificationPublisher`

```csharp
Task PublishAsync(NotificationRequest request, CancellationToken ct = default);
Task PublishAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default);
```

```csharp
public sealed record NotificationRequest(
    string UserId,
    string Type,
    string Title,
    string Message,
    string? Data = null,
    bool SendEmail = false);
```
**Failure guarantee: never throws.** Every Node call site wraps `createNotification` in its own
try/catch and continues, so the guarantee lives in the port — do not add a try/catch, and do not
treat a completed call as proof a row was written. `Type` is free text (`NEW_APPOINTMENT_REQUEST`,
`APPOINTMENT_CONFIRMED`, `APPOINTMENT_CANCELLED`, `PATIENT_ARRIVED`, `INTAKE_NOTE_READY`). `Data` is
opaque JSON as a **string**. Commits through the **Notifications** unit of work, so **call it AFTER
your own save, never inside `ExecuteInTransactionAsync`.**

Which of the 19 notify (appointments.js):

| Endpoint | Notification |
|---|---|
| `POST /` | physician, `NEW_APPOINTMENT_REQUEST`, `sendEmail: true` |
| `POST /walk-in` | physician, `PATIENT_ARRIVED` — **only when the caller is not the physician** |
| `PUT /{id}/confirm` | patient, `APPOINTMENT_CONFIRMED`, `sendEmail: true` |
| `PUT /{id}/cancel` | patient-cancelled → physician (no email); otherwise → patient with email |
| `PUT /{id}/check-in` | physician, `PATIENT_ARRIVED` |
| `POST /{id}/intake-note` | physician, `INTAKE_NOTE_READY` — **only when the caller is not the physician** |
| `PUT /{id}/complete`, `PUT /{id}/no-show`, `POST /recurring`, `DELETE /{id}`, every slot route, every GET | **none** |

### 3.8 `Tebrazi.SharedKernel.Abstractions.IAiGateway`

```csharp
Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken ct = default);
Task<AiTranscriptionResult> TranscribeAsync(AiTranscriptionRequest request, CancellationToken ct = default);
Task<AiDrugCheckResult> CheckDrugsAsync(AiDrugCheckRequest request, CancellationToken ct = default);
bool IsConfigured { get; }
```

```csharp
public sealed record AiChatRequest(
    string System, string User, string? Model = null, double? Temperature = null,
    int? MaxTokens = null, bool UseCache = true, string? Agent = null, string? UserId = null);

public sealed record AiChatResult(
    string Text, string Provider, string? Model,
    int? TokensUsed = null, decimal? CostUsd = null, bool Cached = false);

public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception;
```
`GET /ai-optimize` is the only Appointments caller: `ChatAsync` with `Temperature: 0.2`,
`MaxTokens: 1200`, `UseCache: true`, `Agent: "scheduler"`, `UserId: <caller>`.

**Throwing is normal**, and the current registration
(`UnconfiguredAiGateway`) always throws `AiUnavailableException` and reports `IsConfigured == false`.
Reproduce Node's two-layer catch exactly:
* the **outer** catch (appointments.js:1547-1550) → `insights: []` and **`aiProvider: null`**, stats
  intact, 200;
* the **inner** JSON-parse catch (appointments.js:1543-1546) → `insights: []` but `aiProvider` is the
  **real provider string**, because it is assigned before the parse (appointments.js:1522).

`insights` is always an array, never null, and capped at 6 by `.slice(0, 6)`.

### 3.9 `Tebrazi.SharedKernel.Abstractions.IWaitingQueueWriter` — NEW, no-op

```csharp
Task EnqueueIfAbsentAsync(WaitingQueueEnrolment enrolment, CancellationToken ct = default);
```

```csharp
public sealed record WaitingQueueEnrolment(
    string ClinicId,
    string PhysicianUserId,
    string PatientUserId,
    string PatientName,
    string AppointmentId,
    DateTime QueueDate,
    string? SubprofileId = null,
    string? SubprofileName = null,
    string? Reason = null,
    string Source = "APPOINTMENT");
```
Called by `PUT /{id}/check-in` (appointments.js:1029-1062) and `POST /walk-in`
(appointments.js:624-687). The "already queued today?" test and the `queueNumber` assignment live
**behind** the port, because Node runs them as three unsynchronised queries and the race is the
implementer's to own.

**Failure guarantee: never throws.** Both Node call sites log at warning and continue, and **neither
response body mentions the queue**, so there is nothing to report either way. Call it **after** your
appointment save; do not put it inside `ExecuteInTransactionAsync`.

Field notes for byte parity:
* `PhysicianUserId` is a **user** id. `/walk-in` resolves it from the appointment's physician profile
  and falls back to **the caller's own user id** when that profile cannot be read
  (`physicianProfile?.userId || userId`, appointments.js:673) — reproduce the fallback at the call
  site.
* `PatientName`: `/walk-in` substitutes the **dependant's** name when `subprofileId` resolved, else
  the account display name, else the literal `"Patient"` (appointments.js:658-667). `/check-in`
  **never** substitutes the dependant — display name, else `"Patient"` (appointments.js:1053).
* `SubprofileId`/`SubprofileName`: `/walk-in` only. Both null on the check-in path.
* `Reason`: `/walk-in` passes `reason || "Walk-in"`; `/check-in` passes the appointment's own reason
  or null.
* `Source` is `"APPOINTMENT"` on **both** paths, including `/walk-in` (appointments.js:679, :1055),
  even though the column default is `"WALK_IN"`.
* `QueueDate` is **local midnight** of the day (`new Date(y, m, d)`, appointments.js:627, :1031).

**No read method, deliberately** — see [§8 GET /queue](#get-queue-needs-no-waiting-room-read).

### 3.10 `Tebrazi.SharedKernel.Abstractions.IPaymentWriter` — NEW, no-op

```csharp
Task<AppointmentPaymentSummary?> CreateForAppointmentIfAbsentAsync(
    AppointmentPaymentRequest request, CancellationToken ct = default);
```

```csharp
public sealed record AppointmentPaymentRequest(
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string AppointmentId,
    double Amount,
    string Description,
    string Currency = "EGP",
    string Method = "CASH",
    string Status = "PENDING");

public sealed record AppointmentPaymentSummary(string Id, double Amount, string? Description);
```
Called by `PUT /{id}/check-in` only (appointments.js:1064-1106). The **fee decision stays in the
handler**: `CountCompletedAsync > 0` selects follow-up, then
`isFollowUp ? (followUpFee is null or 0 ? 200 : followUpFee) : (consultationFee is null or 0 ? 300 : consultationFee)`.
Skip the call entirely when the clinic row could not be read (`if (clinic)`, appointments.js:1072) —
that path yields `payment: null`.

The duplicate guard behind the port is a bare `findFirst({ where: { appointmentId } })` with no
status filter, so a CANCELLED or REFUNDED payment still suppresses a new one.

**Failure guarantee: never throws; returns null instead.** `null` maps straight to the response's
`"payment": null`, which Node itself produces in three situations — a payment already existed, no
clinic row, or the write threw. So a caller **must not** read null as "no payment exists", and must
not surface it as an error.

`AppointmentPaymentSummary` is **exactly** the three keys the response's `payment` object carries
(appointments.js:1112). `Amount` is a Prisma `Float` → plain JSON number.

### 3.11 `Tebrazi.SharedKernel.Abstractions.IPatientNoteWriter` — NEW, no-op

```csharp
Task<PatientNoteRecord?> UpsertNoteAsync(PatientNoteUpsert request, CancellationToken ct = default);
```

```csharp
public sealed record PatientNoteUpsert(
    string PhysicianUserId,
    string PatientUserId,
    string Content,
    string? MatchContentPrefix = null);

public sealed record PatientNoteRecord(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Content,
    bool IsPinned,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
```
Called by `POST /{id}/intake-note` only (appointments.js:1155-1198).

* `PhysicianUserId` is the **appointment's physician user id** when a staff member wrote the note,
  and the **caller's own id** when the caller is the physician (appointments.js:1153).
* `Content` is written verbatim — the route builds `$"[INTAKE by {callerUserId}]\n{note.Trim()}"`.
* `MatchContentPrefix` is the dedupe `startsWith`. Pass the literal `"[INTAKE]"` to stay faithful:
  it **never matches** what the route writes (`"[INTAKE by …]"`), so the update branch is dead code
  and every call creates a row (audit-2).

**This port is NOT best-effort.** The note *is* the endpoint's product — its id, content and
timestamps are the 200 body — so unlike §3.7/§3.9/§3.10 a null result is a failure the handler must
surface. See [§8 intake-note](#intake-note-is-a-documented-500-until-patientnotes-lands).

---

## 4. Caller context, pagination and committing

### `Tebrazi.SharedKernel.Abstractions.ICurrentUser`

```csharp
bool IsAuthenticated { get; }
string? UserId { get; }
string? Email { get; }
string? DisplayName { get; }
string? Role { get; }
string? UserType { get; }
string? OrganizationId { get; }
```
Claim names mirror Node exactly. `UserType` is the **claim**, not the database — `GET /`'s
physician/patient fork is decided on `currentUser.UserType == "PHYSICIAN"`, so a userType changed in
the DB does not take effect until the token is reissued.

House rule from Visits, follow it: **READ handlers inject `ICurrentUser` and resolve the caller
themselves; WRITE handlers take the caller id as an explicit command parameter** (`string CallerUserId`).
Be consistent inside your own file.

For a missing `UserId`, Visits throws
`new BusinessException("No token provided", "No token provided", 401)` — the literal Node body from
`authCheck`. Unreachable behind `[Authorize]`, and kept so a null id cannot silently become a filter
value. There is no shared `AppointmentsCaller` helper; declare an `internal static` one in your own
file if you want it.

### `Tebrazi.SharedKernel.Abstractions.IClinicContext`

```csharp
string? ClinicId { get; }
```
Resolves `X-Clinic-Id`, then the `clinicId` query string. **`GET /` cannot use this**: it needs the
header and the query value **separately** (`req.headers['x-clinic-id'] || clinicId` for the staff
check, then an unconditional `if (clinicId) where.clinicId = clinicId` that **overwrites** the
staff-validated clinic). Bind the raw header yourself for that endpoint. See §8.

### `Tebrazi.SharedKernel.Pagination`

```csharp
public readonly record struct PageRequest(int Page, int Limit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public int Skip { get; }                                   // (Page - 1) * Limit
    public static PageRequest Parse(string? page, string? limit);
}

public sealed record PaginationMeta(int Total, int Page, int Limit, int Pages)
{
    public static PaginationMeta From(int total, PageRequest request);
    public static PaginationMeta Empty(PageRequest request);    // total 0, pages 0, parsed page/limit
}
```
Reproduces JavaScript's `||`: `limit=0` → 20, `limit=-5` → 1, `"50abc"` → 50, cap 100. Page and
limit travel as `string?`, never `int?`.

> **None of the 19 Appointments endpoints paginates.** `GET /` is `take: 200` with no metadata;
> `GET /today`, `GET /queue`, `GET /available`, `GET /slots` return everything. Listed here so
> nobody adds pagination the client is not expecting.

### `Tebrazi.SharedKernel.Abstractions.Persistence.IDbContext` — how a handler commits

Handlers inject **`IAppointmentsDbContext`** (which is `IDbContext` with no extra members):

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
A transaction helper **does** exist, and for these 19 endpoints you should almost never use it:

* Retry-on-failure is enabled, so the delegate **may run more than once**. Keep it to database work.
* `ExecuteInTransactionAsync` **changes observable behaviour** on the two destructive slot routes and
  on `POST /recurring` — see §8.
* Never call `INotificationPublisher`, `IWaitingQueueWriter`, `IPaymentWriter`,
  `IPatientNoteWriter` or `IClinicWorkingHoursWriter` inside it: each commits through **another
  module's** unit of work, so it would not be covered, and it would re-run on retry.

The Begin/Commit/Rollback trio is only safe inside an execution strategy — do not touch it.

---

## 5. Exception → status → body

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

### The rule for Appointments

**Every error body in appointments.js is a bare `{ "error": "…" }`.** So:

| Node | Throw this |
|---|---|
| `400 {"error":"All booking fields are required"}` | `new BusinessException(m, m, 400)` |
| `403 {"error":"Not your clinic"}` | `new ForbiddenException(m)` |
| `404 {"error":"Appointment not found"}` | `new NotFoundException(m)` |
| `409 {"error":"This slot is already booked"}` | `new BusinessException(m, m, 409)` |
| `500 {"error":"Failed to list appointments"}` | `new BusinessException(m, m, 500)` |

> **Do NOT use `ValidationException` for a 400 or `ConflictException` for a 409 in this module.**
> Both prepend a generic `error` label (`"Validation failed"` / `"Conflict"`) and push the real
> message into `message`, which is not the shape the client matches on. Visits keeps a one-line
> helper for this (`VisitWriteErrors.BadRequest`); declare your own `internal static` equivalent
> per file — for example
> `internal static class AppointmentErrors { public static BusinessException Node(string error, int status = 400) => new(error, error, status); }`
> — and do **not** put it in a shared file, or five parallel files will declare the same type in one
> namespace and none of them will compile.

Model-binding failures never reach a handler: `Program.cs` renders them as
`400 {"error":"Validation failed","message":<first error>}`. Bind bodies as nullable records so an
absent/`null` body reaches your own guard rather than the binder's.

---

## 6. Enums

`Tebrazi.Appointments.Domain.Entities`:

```csharp
public enum AppointmentStatus { PENDING, CONFIRMED, COMPLETED, CANCELLED, NO_SHOW }
public enum AppointmentType  { IN_PERSON, VIDEO, PHONE, WALK_IN }
```
Both persist and serialize **as strings**. `AppointmentType` has no `TELEMEDICINE` member; `VIDEO`
is the video type.

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
Appointments touches **none of these as a type** — `ICurrentUser.UserType` is a `string?`, and the
fork is `currentUser.UserType == "PHYSICIAN"`. Do not parse it into `UserType`; the claim can carry
a value the enum does not have.

Strings that are **not** enums and must stay strings on the wire:

* `VisitQueueRow.Status` — `IN_PROGRESS | COMPLETED | CANCELLED | ARCHIVED` (§3.6).
* `SubprofileSummary.Relation` — `SELF | SPOUSE | CHILD | PARENT | SIBLING | OTHER`.
* `Appointment.RecurringRule` — `WEEKLY | BIWEEKLY | MONTHLY`, unvalidated free text; anything not
  `WEEKLY` or `BIWEEKLY` yields a 30-day interval (appointments.js:714).
* `NotificationRequest.Type`.
* `GET /ai-optimize`'s insight `type` (`NO_SHOW | SLOT_DURATION | PEAK_HOURS | UTILIZATION | CANCELLATION | THROUGHPUT`,
  anything else coerced to `THROUGHPUT`) and `impact` (`HIGH | MODERATE | LOW`, else `MODERATE`).

---

## 7. `using` list for a new use-case file

Place handlers in `Modules/Appointments/Tebrazi.Appointments.Application/UseCases/`, namespace
`Tebrazi.Appointments.Application.UseCases`.

```csharp
using System.Text.Json;                                            // JsonElement bodies, JsonSerializer
using Tebrazi.Appointments.Application.Abstractions.Persistence;   // IAppointmentsDbContext, IAppointmentStore, ITimeSlotStore, AppointmentFilter, BookedSlot
using Tebrazi.Appointments.Application.ApiModels.Responses;        // MessageResponse (+ your own DTOs)
using Tebrazi.Appointments.Domain.Entities;                        // Appointment, TimeSlot, AppointmentStatus, AppointmentType
using Tebrazi.SharedKernel.Abstractions;                           // ICurrentUser, IClinicContext, INotificationPublisher, NotificationRequest,
                                                                   // IAiGateway + Ai* records, IWaitingQueueWriter, IPaymentWriter, IPatientNoteWriter
using Tebrazi.SharedKernel.Abstractions.Directory;                 // IIdentityDirectory, IClinicDirectory, IClinicWorkingHoursWriter,
                                                                   // IPatientDirectory, IClinicPatientDirectory, IVisitDirectory + their records
using Tebrazi.SharedKernel.Exceptions;                             // BusinessException, NotFoundException, ForbiddenException, …
using Tebrazi.SharedKernel.Mediation;                              // IRequest<TResult>, IRequestHandler<TRequest,TResult>
```
Optional, add only when used: `using System.Globalization;` (`ToString`/`Parse` with an explicit
culture), `using System.Text.Json.Nodes;` (`JsonNode` pass-through columns),
`using Tebrazi.SharedKernel.Pagination;` (nothing here needs it).

`ImplicitUsings` is enabled, so `System`, `System.Linq`, `System.Collections.Generic` and
`System.Threading.Tasks` are already in scope. `Tebrazi.SharedKernel` arrives transitively through
`Tebrazi.Appointments.Domain` — no csproj edit needed.

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
One request record immediately above its handler, both in the same file. `AddHandlersFromAssembly`
in `AppointmentsModule` discovers them automatically — **no DI edit is needed for a new handler**.

Response type per endpoint. When two variants of one endpoint have incompatible key sets, the
Visits precedent is `IRequest<object>` and returning `(object)` rows.

### Name collisions across the five files

You are all writing into **one namespace**. Prefix every type you declare with your group
(`SlotXxx`, `ApptReadXxx`, `BookingXxx`, `StatusXxx`, `AssistantXxx`) and do not declare any of
these, which already exist: `MessageResponse`, `AppointmentFilter`, `BookedSlot`, `Appointment`,
`TimeSlot`, `AppointmentStatus`, `AppointmentType`.

---

## 8. Gaps and decisions

### `GET /queue` needs no waiting-room read

The brief assumed `GET /queue` reads `waiting_queues`. **It does not.** Node
(appointments.js:1236-1310) queries `appointments` for the clinic's day, N+1s `users` for names,
derives `checkedIn` / `checkedInAt` / `room` by **parsing the `CHECKIN:` marker out of
`appointments.notes`**, and overlays `visits` for the day. Every one of those is either the
Appointments module's own table or already behind `IVisitDirectory.ListForClinicDayAsync` /
`IIdentityDirectory`.

**Decision: `IWaitingQueueWriter` has no read method, and `GET /queue` returns fully real data with
every no-op in place.** Adding a read method nothing calls would have implied a data source the
endpoint does not have, and a no-op "empty queue" would have been fabrication in the other
direction. Write the endpoint against the appointment + visit data and the notes marker; it is
complete.

What *is* lost while `IWaitingQueueWriter` is a no-op: the physician's **waiting-room screen** (a
different module's endpoints) never sees a patient checked in through Appointments. `GET /queue` is
unaffected because it never looked there.

### `payment: null` is a real Node value, not a fudge

`UnimplementedPaymentWriter` returns null, and `PUT /{id}/check-in` renders that as
`"payment": null` — which Node itself produces on a repeat check-in, when the clinic row is missing,
and when the write throws. So the endpoint stays byte-valid. The **loss** is that against the no-op
`payment` is *always* null, so the client can never distinguish "fee due" from "already handled".

### intake-note is a documented 500 until PatientNotes lands

`POST /{id}/intake-note` returns `{ ...savedNote, message: 'Intake note saved' }` — the stored row's
id, content and timestamps **are** the body. There is no honest 200 without a stored row, and
synthesising one would report a note nobody can read back.

**Decision: `UnimplementedPatientNoteWriter` returns null, and the handler must answer Node's own
failure body for this route** — `new BusinessException("Failed to save intake note", "Failed to save intake note", 500)`,
matching appointments.js:1199-1202. Everything *before* the write is still ported for real: the 400
on a blank note, the 404, the physician/staff 403, the physician-user resolution, and the
`INTAKE_NOTE_READY` notification (which must **not** fire when there is no note — Node only notifies
after a successful save). The placeholder logs at warning level, so the reason is in the log.

### The `PatientNote` NULL-uniqueness difference (real, not paperable)

`PatientNote` carries `@@unique([physicianUserId, patientUserId, subprofileId])` and this handler
always writes `subprofileId = NULL`. **PostgreSQL treats NULLs as distinct**, so Node's repeated
creates all succeed and quietly pile up duplicate rows that note readers then show twice. **SQL
Server treats NULLs as equal**, so the second intake note for a physician/patient pair raises a
duplicate-key error → `500 "Failed to save intake note"` where Node returns 200. The same collision
hits any `PatientNote` written by `routes/notes.js` for that pair.

Whoever ports PatientNotes **must** use a NULL-tolerant filtered unique index (or drop the
constraint) or the endpoint is single-use per pair. Recorded here and in `IPatientNoteWriter`'s XML
doc; **not** worked around in Appointments.

### `POST /api/appointments/slots` is a known parity gap

appointments.js:57-88 is the router's 20th registration and is **out of scope**: audit-2 confirms no
caller anywhere in `client/src`, and this port targets client-called endpoints. It was not missed.
For the record, its contract is: body `{ clinicId, slots: [{ dayOfWeek, startTime, endTime, slotDuration? }] }`;
`400 "clinicId and slots array required"`, `400 "Physician profile required"`, `403 "Not your clinic"`;
`201 { count, message: "<count> slots created" }` (always plural, byte-identical to `/slots/bulk`);
per-slot `slotDuration || 30`; and — unlike `/slots/bulk` and `/slots/sync-from-hours` — **no
`deleteMany` first**, so it appends and can create exact duplicates.

### destructive-then-fail is the contract

`POST /slots/bulk` and `POST /slots/sync-from-hours` both `deleteMany` **first** and can then fail:
bulk answers `400 "Time range too short for slot duration"` after the delete has already run
(appointments.js:144 then :170), and either route's `createMany` can throw. **Node leaves the
deletion committed**, so the physician is left with no slots at all.

`ITimeSlotStore.DeleteAllAsync` reproduces this: `ExecuteDeleteAsync` issues one DELETE that is
committed the moment it returns, independently of the later `SaveChangesAsync` for the new rows.

**Wrapping the delete and the create in `ExecuteInTransactionAsync` WOULD change observable
behaviour** — it would roll the deletion back, so the client would keep its old slots where Node
loses them. Do not do it. Same reasoning for `POST /recurring`: `Add` + save per occurrence, so a
failure on occurrence *k* leaves *k-1* rows exactly as Node does.

### Timezone: the systemic risk across this file

Node builds its day windows in **server-local** time (`setHours(0,0,0,0)`, `new Date(y,m,d)`,
`new Date(date + 'T00:00:00')`) while `POST /` stores `new Date(appointmentDate)` = **UTC midnight**.
On a server with a negative UTC offset the stored row falls outside `GET /available`'s local-day
window, so `isBooked` is silently always false and the client is offered already-booked slots
(audit-2, `breaks-client`). `POST /recurring` advances dates with local-time `setDate`, so a DST
transition inside a series shifts later occurrences' stored UTC time-of-day by an hour.

The store's day windows are `date.Date` .. `.AddDays(1)`, i.e. **whatever kind the `DateTime` you
pass carries**. Be explicit and consistent per endpoint about which day you mean, and record any
divergence in `docs/PORT-STATUS.md` rather than silently "fixing" it — several of these bugs are
what the client currently renders.

### Authorization defects that must be reproduced

Reproduce, and flag separately — do not fix:

* **`GET /`**: `if (clinicId) where.clinicId = clinicId` (appointments.js:382) **overwrites** the
  staff-validated clinic set at :372. A user with an active staff row at clinic A who sends
  `X-Clinic-Id: A` and `?clinicId=B` passes the check on A and then receives **clinic B's**
  appointments, patient-enriched. (audit-2, `breaks-client`.)
* **`GET /queue`**: the physician branch requires clinic **ownership**, so a physician who merely
  works at the clinic gets `403 "Not staff at this clinic"` unless they also hold a staff row. And
  the appointment query has **no `physicianId` filter**, so every physician's appointments at that
  clinic go to any staff member.
* **`POST /`**: `subprofileId` is written with **no ownership validation** — any patient can attach a
  stranger's dependant id, and `GET /` then echoes that dependant's name and relation back.
* **`PUT /{id}/cancel`** allows `isPatient`; `/confirm`, `/complete`, `/no-show` do not.

### `GET /` is cached for 15 seconds in Node, and the key excludes `X-Clinic-Id`

`cacheMiddleware(15)` keys on `route:${userId}:${req.originalUrl}` (appointments.js:352), which does
**not** include the `X-Clinic-Id` header the handler branches on. The client mutates that header on
context switch and re-requests the same URL, so for up to 15 s after switching clinics a staff user
is served the previous clinic's array. This port adds **no cache**, so the .NET backend is
consistently fresher than Node here. That is a deliberate divergence — record it in
`docs/PORT-STATUS.md`; do not build a header-blind cache to match it. `GET /today` is cached the same
way but is physician-scoped, so it is benign.

### "no physician profile" is answered four different ways

Do not harmonise these:

| Endpoint | No `PhysicianProfile` |
|---|---|
| `GET /slots` | `200 []` |
| `GET /today` | `200 []` |
| `GET /` (physician branch) | `200 []` |
| `GET /available` (for the *target* physician) | `200 []` |
| `POST /slots/bulk`, `POST /slots/sync-from-hours` | `400 {"error":"Physician profile required"}` |
| `POST /`, `POST /recurring` (target physician) | `400 {"error":"Physician not found"}` |
| `GET /ai-optimize` | **`403 {"error":"Only physicians can access scheduling insights"}`** — the only 403 GET in the router (audit-2; the chunk file omits this branch) |
| `POST /walk-in` | falls through to the staff path, then `403 {"error":"Not authorized"}` |

### Numbers in `GET /ai-optimize` that a port gets wrong

From audit-2, which overrides the chunk file:

* `slotUtilization` is `Math.min(x, 1)` — saturates at exactly 1, and `1` serializes as the integer
  `1`, not `1.0`.
* `avgDailyPatients` is `toFixed(**1**)`, not 3 decimals like every other rate.
* `avgVisitDurationMin` is a rounded **int or null** — null when no COMPLETED visit has a
  `completedAt` in the window, or when every computed duration falls outside the exclusive
  `(0, 300)` minute band.
* `avgSlotDuration` falls back to the literal `30` when there are no active slots.
* `typeBreakdown` is a **sparse** object keyed only by the `AppointmentType` values actually
  present — an absent type has **no key**, it is not 0. A `Dictionary` pre-seeded with all four
  diverges.
* `hourDistribution` is sorted by `localeCompare` on the `"9:00"`-style string, safe only because
  `startTime` is zero-padded in practice.
* `peakHour` defaults to `"09"` → `"09:00"`; `busiestDay` defaults to the literal `"N/A"`.
* Fewer than 5 appointments in the window → `200 { stats: null, insights: [], message: "…" }`, a
  **different envelope** with no `aiProvider` key at all.

### `GET /queue` shape and arithmetic

Row key order: the 22 scalars, then `subprofile`, `physician` (the **full** profile plus a nested
`user: { displayName }`), then `patientUser`, `checkedIn`, `checkedInAt`, `room`, then `visitStatus`,
`visitFollowUp`, `visitFollowUpNotes`, `visitCompletedAt`. `patientUser` is exactly
`{ displayName, phone }` and is **`null`** (not `{}`) for a dangling `patientUserId` — and
`appointments` has no FK on that column, so dangling ids happen.

The summary buckets **do not reconcile and must not be fixed**:
`pending = !checkedIn && status != 'COMPLETED'` counts `NO_SHOW` rows, so every un-checked-in
no-show is in **both** `pending` and `noShow`, and `total != arrived + pending + completed`
(appointments.js:1302-1308).

### Route-order hazards (audit-2)

Node has **no** `GET /{id}`, `PUT /{id}` or `POST /{id}` at all, so nothing currently swallows the
literals. The moment this port adds one, these must be mapped **first** or as literal-precedence
attribute routes: `slots`, `slots/bulk`, `slots/sync-from-hours`, `today`, `queue`, `available`,
`ai-optimize`, `walk-in`, `recurring`, and the second-segment literals `confirm`, `cancel`,
`complete`, `no-show`, `check-in`, `intake-note`. Kebab-case exactly as written; **never** a
`PUT /{id}/{action}` or `POST /{id}/{action}` catch-all.

One live Node behaviour a port must not change: `DELETE /api/appointments/slots` with no id has no
route of its own and binds to `DELETE /{id}` with `id = "slots"`, returning
`404 {"error":"Appointment not found"}` — not a route miss and not a 405.

### Response-shape traps worth repeating

* **No shared appointment DTO.** `GET /`'s `physician` is `{ specialty, user: { displayName } }`
  with **no id**; `POST /`'s is `{ user: { displayName } }` with **no specialty** — same relation
  name, two different DTOs. `GET /today`'s `clinic` is `{ name }` only and it **also** includes
  `subprofile` (the chunk file omits that). `POST /walk-in`'s 201 carries `clinic: { name }` and
  **nothing else**, even when `subprofileId` was supplied.
* **`PUT /{id}/check-in`** appends **three** keys after the 22 scalars, in this order: `message`,
  `checkedInBy` (a bare user-id string, duplicating what is inside the notes JSON), `payment`.
* **`POST /walk-in`** fixes values the request cannot override: `reason` defaults to the literal
  `"Walk-in"` (never null), `notes` is the literal `"WALK_IN"`, `confirmedAt` is always set, and
  `endTime` falls back to the resolved start time, producing a **zero-length** appointment.
* **`POST /recurring`** answers `{ count, groupId, appointments: [...] }` — the created rows carry
  **no relation objects** (bare `create`, no `include`) — clamps `count` to
  `Math.min(count || 4, 12)`, sends **no notification**, and runs **no conflict check**, so it can
  stack a series on top of existing PENDING/CONFIRMED bookings and never returns 409.
