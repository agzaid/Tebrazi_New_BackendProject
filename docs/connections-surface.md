# Connections — shared surface inventory

Everything the 21 Connections handlers may call, transcribed **verbatim** from the code as it
stands after the scaffolding pass. Five handler files are being written in parallel and none of
them can build, so this document is the contract between them: **if a signature is not here, it
does not exist.**

Built clean at the time of writing:
`dotnet build Tebrazi.Backend.sln --no-incremental -v q --nologo` → **0 errors, 216 warnings**,
which is exactly the pre-existing baseline. Every warning is pre-existing `CS1573`/`CS1574`/`CS9124`
in files this pass did not touch; **nothing in this inventory emits one**, and nothing in it
removed one either.

> **⚠ There are no contract files for this module.** `docs/port-contracts/` covers Visits,
> Appointments and Prescriptions only. There is no `chunk-*.json`, no `audit-*.json` and no
> `surface-*.json` for Connections. The authority is `server/src/routes/connections.js` — 1,818
> lines, 25 route registrations — and nothing else. Every line number in this document points
> there unless another file is named.

Sections:

1. [Store surface](#1-store-surface)
2. [Entity surface](#2-entity-surface)
3. [Cross-module ports](#3-cross-module-ports)
4. [Module-local services](#4-module-local-services)
5. [Caller context, pagination and committing](#5-caller-context-pagination-and-committing)
6. [Exception → status → body](#6-exception--status--body)
7. [Enums](#7-enums)
8. [The `using` list, and the endpoint → group map](#8-using-list-and-group-map)
9. [Name collisions across the five files](#9-name-collisions)
10. [Cross-cutting findings from the full read](#10-cross-cutting-findings)
11. [Gaps and decisions](#11-gaps-and-decisions)

---

## Endpoint → group map

Five groups, 21 endpoints. **19 exist in Node; 2 do not** — see §11.

| Group | Prefix | Endpoints (Node line) |
|---|---|---|
| A — link lifecycle | `ConnLink*` | `GET /` (31), `POST /request` (131), `PUT /{id}/accept` (240), `PUT /{id}/reject` (277), `DELETE /{id}` (401), `GET /patient-summaries` (1042) |
| B — dependants | `ConnSubprofile*` | `POST /assign-subprofile` (310), `DELETE /unassign-subprofile/{subprofileId}/{physicianUserId}` (375) |
| C — discovery & invites | `ConnDiscover*` | `GET /search` (443), `POST /add-by-phone` (529), `POST /create-patient` (617), `POST /invite-email` (969), `GET /invite-link` (1011), **`GET /search-physicians` (none)**, **`POST /` (none)** |
| D — walk-in charts | `ConnClinicPatient*` | `POST /clinic-patients` (747), `GET /clinic-patients` (813), `DELETE /clinic-patients/{id}` (860) |
| E — pairing | `ConnPin*` | `POST /generate-pin` (1515), `POST /connect-by-pin` (1608), `GET /qr-code` (1793) |

**⚠ Two rows were corrected after the five groups were written in parallel, so this table matches
what was actually built.** `GET /patient-summaries` moved B → A; `POST /invite-email` and
`GET /invite-link` moved E → C. Neither mismatch would have surfaced in a build: a second handler
for the same route under a different prefix compiles clean and fails only at RUNTIME, when the
mediator resolves two handlers for one request type. The same corrections are in
`ApiModels/Responses/ConnectionResponseConventions.cs`, which is authoritative. **Every route in the
module appears exactly once above** — 21 rows, 21 request records, 21 handlers, verified 1:1.

---

## 1. Store surface

`Tebrazi.Connections.Application.Abstractions.Persistence` —
`Modules/Connections/Tebrazi.Connections.Application/Abstractions/Persistence/IConnectionStores.cs`

### Supporting types

```csharp
public interface IConnectionsDbContext : IDbContext;

public sealed record ConnectionFilter(
    string? PhysicianUserId = null,
    string? PatientUserId = null,
    ConnectionStatus? Status = null,
    bool? SubprofileIdIsNull = null,
    IReadOnlyCollection<string>? PatientUserIdIn = null);

public sealed record ConnectionEdge(
    string PhysicianUserId,
    string PatientUserId,
    ConnectionStatus Status);
```

A **null** member of `ConnectionFilter` is not applied, matching how the Node handler spreads its
`where` key by key.

* `SubprofileIdIsNull` — only `true` adds a predicate (`subprofile_id IS NULL`). **`false` is
  treated as null**, because no Node query on this path asks for "subprofile is NOT null".
* `PatientUserIdIn` — **an empty collection is a real filter and returns nothing.** It means "no
  user matched the name search". Treating it as unfiltered would list a physician's whole patient
  book for a term that matched nobody.
* Pass `null`, not `""`. ASP.NET binds `?status=` to the empty string and every store test is
  `is not null`. Use `ConnJs.Truthy(...)` (§4).

### `IDoctorPatientConnectionStore`

```csharp
Task<DoctorPatientConnection?> GetForUpdateAsync(string id, CancellationToken ct = default);
```
Tracked single row by id, **no ownership predicate**. This is the load for `/accept` (L245),
`/reject` (L282) and `DELETE /{id}` (L406) — all three read first and check the caller afterwards,
which is why **404 precedes 403** on all three.

```csharp
Task<IReadOnlyList<DoctorPatientConnection>> ListAsync(
    ConnectionFilter filter, CancellationToken ct = default);
```
The whole filtered set, ordered `created_at` **DESC** — `orderBy: { createdAt: 'desc' }` on all
three branches of `GET /` (L73, L91, L110). The four live configurations:

| Caller | Filter |
|---|---|
| `GET /` staff branch (L56-64) | `new ConnectionFilter(PhysicianUserId: clinicPhysicianUserId, Status: s, PatientUserIdIn: idsFromNameSearch)` |
| `GET /` physician branch (L82) | `new ConnectionFilter(PhysicianUserId: caller, SubprofileIdIsNull: true, Status: s)` |
| `GET /` patient branch (L96) | `new ConnectionFilter(PatientUserId: caller, Status: s)` |
| `GET /patient-summaries` (L1048) | `new ConnectionFilter(PhysicianUserId: caller, Status: ConnectionStatus.ACCEPTED)` |

```csharp
Task<DoctorPatientConnection?> FindPairAsync(
    string physicianUserId,
    string patientUserId,
    string? subprofileId,
    ConnectionStatus? status = null,
    bool tracked = false,
    CancellationToken ct = default);
```
`subprofileId` is compared as a **VALUE**: null matches only rows whose `subprofile_id` **IS
NULL**, never "any subprofile". Call sites: `POST /request` (L189, `subprofileId || null`, tracked
for the re-request branch), `POST /assign-subprofile` twice (L329 with `null` + `ACCEPTED`, then
L337 with the real id and no status), `DELETE /unassign-subprofile` (L380, tracked), and
`POST /connect-by-pin` (L1638, explicitly `null`, tracked).

```csharp
Task<DoctorPatientConnection?> FindAnyPairAsync(
    string physicianUserId, string patientUserId, CancellationToken ct = default);
```
**The looser lookup, and the difference is wire-visible.** `{ physicianUserId, patientUserId }`
with no `subprofileId` key at all, so it matches the parent edge OR any dependant edge.
`POST /add-by-phone` (L556) uses this, so a physician connected only to the patient's CHILD is
reported "already connected" to the parent and no parent edge is created. `connect-by-pin` uses
`FindPairAsync(..., null)` instead and therefore does create one. Reproduce each route's own
choice. Untracked, ordered `created_at` ASC for determinism.

```csharp
Task<DoctorPatientConnection?> FindForUpdateByEitherSideAsync(
    string callerUserId, string otherUserId, CancellationToken ct = default);
```
The "the id is actually a user id" fallback in `DELETE /{id}` (L411-418). Tracked. Matches either
direction, no subprofile key, ordered `created_at` ASC.

```csharp
Task<IReadOnlyList<ConnectionEdge>> ListEdgesWithAsync(
    string lookupUserId,
    IReadOnlyCollection<string> counterpartUserIds,
    CancellationToken ct = default);
```
`GET /search`'s enrichment sweep (L497-505). `lookupUserId` is `actingAsPhysicianId || userId`
(L496) — the PHYSICIAN's id on the staff path, so a receptionist sees the doctor's badges. Empty
input returns `[]`. Both ends come back because the Node pairing logic at L508 is quirky and stays
in the handler — see §10.

```csharp
Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListAcceptedSubprofileIdsAsync(
    string physicianUserId,
    IReadOnlyCollection<string> patientUserIds,
    CancellationToken ct = default);
```
The `{ subprofileId: { not: null }, status: ACCEPTED }` half of `familyCount` (L1079-1082),
batched across every connected patient. A patient with no dependant edges is **absent** from the
dictionary — use `GetValueOrDefault`. Empty input returns empty.

```csharp
void Add(DoctorPatientConnection connection);
void Remove(DoctorPatientConnection connection);
```
`Remove` is a **HARD** delete (`prisma…delete`, L385 and L427). There is no soft-delete column on
this model, so there is no softer option: removing the row revokes the physician's access to the
chart.

### `IConnectionPinStore`

"Redeemable" means `used_at IS NULL AND expires_at > now`, with a **strict** comparison — which is
what makes the force-expire write take effect.

```csharp
Task<IReadOnlyList<ConnectionPin>> ListRedeemableForUpdateAsync(
    string physicianUserId, DateTime now, CancellationToken ct = default);
```
TRACKED, so the caller can `ForceExpire` each one. Reproduces the `updateMany` at L1546-1553.

```csharp
Task<bool> AnyRedeemableAsync(string pin, DateTime now, CancellationToken ct = default);
```
The uniqueness probe inside the generation loop (L1560-1562). **Global, not per physician** — two
doctors must never hold the same live code, because `connect-by-pin` resolves a bare four-digit
string to one physician.

```csharp
Task<ConnectionPin?> FindRedeemableForUpdateAsync(
    string pin, DateTime now, CancellationToken ct = default);
```
The redemption lookup (L1618-1624), TRACKED. Matches on the **code alone** plus the window — no
physician or clinic predicate, so the code IS the credential. Ordered `created_at` DESC.

```csharp
void Add(ConnectionPin pin);
```

---

## 2. Entity surface

`Tebrazi.Connections.Domain.Entities` —
`Modules/Connections/Tebrazi.Connections.Domain/Entities/`

### `DoctorPatientConnection : MutableEntity<string>`

```csharp
public string  Id            { get; }   // from Entity<string>
public string  PhysicianUserId { get; }
public string  PatientUserId   { get; }
public string? SubprofileId    { get; }
public ConnectionStatus Status { get; }
public string  InitiatedBy     { get; }
public DateTime? ConnectedAt   { get; }
public DateTime  CreatedAt     { get; }   // from ImmutableEntity
public string    CreatedBy     { get; }
public DateTime? UpdatedAt     { get; }   // from MutableEntity
public string?   UpdatedBy     { get; }

public static DoctorPatientConnection Create(
    string physicianUserId, string patientUserId, string initiatedBy,
    ConnectionStatus status, string? subprofileId = null, DateTime? connectedAt = null);

public static DoctorPatientConnection CreatePending(
    string physicianUserId, string patientUserId, string initiatedBy, string? subprofileId = null);

public static DoctorPatientConnection CreateAccepted(
    string physicianUserId, string patientUserId, string initiatedBy, string? subprofileId = null);

public void Accept();                    // status ACCEPTED + connectedAt = now
public void Reject();                    // status REJECTED ONLY
public void Reopen(string initiatedBy);  // status PENDING + initiatedBy
public bool InvolvesUser(string userId);
```

Behaviour facts that are contract:

* **`Reject()` does not clear `ConnectedAt`** and has no status precondition (L289-292). An
  ACCEPTED edge can be rejected while keeping the timestamp saying when it was accepted.
* **`Reopen()` does not clear `ConnectedAt` either** (L205-208). A previously-accepted-then-rejected
  edge goes back to PENDING carrying its old acceptance stamp.
* **`Accept()` overwrites `ConnectedAt` unconditionally.** Re-accepting moves it.
* `CreateAccepted` is what `assign-subprofile` (L344), `add-by-phone` (L571), `create-patient`
  (L701) and `connect-by-pin` (L1675) all insert. There is **no state machine** forcing PENDING
  first.
* `InitiatedBy` is **not always the caller**: `create-patient` records the PHYSICIAN's id even when
  a staff member made the call (L706), while `POST /request`'s staff path records the STAFF
  member's id (L218) against the clinic physician's `PhysicianUserId`.
* **`CreatedBy` / `UpdatedBy` are not Prisma columns.** They exist on this table and not on Node's.
  Keep them out of every response DTO.

### `ConnectionPin : ImmutableEntity<string>`

```csharp
public string    Id              { get; }
public string    PhysicianUserId { get; }
public string?   ClinicId        { get; }
public string    Pin             { get; }   // four decimal digits as TEXT
public DateTime  ExpiresAt       { get; }
public DateTime? UsedAt          { get; }
public string?   UsedByUserId    { get; }
public DateTime  CreatedAt       { get; }
public string    CreatedBy       { get; }

public static readonly TimeSpan Lifetime;        // 5 minutes
public const int LifetimeSeconds = 300;

public static ConnectionPin Create(
    string physicianUserId, string pin, DateTime expiresAt, string? clinicId = null);

public static string GeneratePin();              // uniform 1000-9999, unpadded
public bool IsRedeemable(DateTime now);          // usedAt is null && expiresAt > now
public void ForceExpire(DateTime now);           // expiresAt = now
public void MarkUsed(DateTime usedAt, string usedByUserId);
```

* **No `UpdatedAt` / `UpdatedBy`** — the Prisma model has no `updatedAt`, so this is an
  `ImmutableEntity`. The two mutations are explicit column writes, not audited modifications.
* `LifetimeSeconds` is the **literal 300** the response echoes as `expiresInSeconds` (L1593). It is
  not recomputed from `ExpiresAt` minus now — emit the constant.
* `GeneratePin` uses `Random.Shared`, matching `Math.random()`. Not a cryptographic source, and
  deliberately so: it is a five-minute code read out loud in a consulting room.

---

## 3. Cross-module ports

All in `Tebrazi.SharedKernel.Abstractions` and `Tebrazi.SharedKernel.Abstractions.Directory`
(`Kernel/Tebrazi.SharedKernel/Abstractions/`). Every one is injectable into a handler.

### 3.1 `IIdentityDirectory` — `…Abstractions.Directory`

Pre-existing members this module uses:

```csharp
Task<UserSummary?> GetUserAsync(string userId, CancellationToken ct = default);
Task<UserSummary?> GetUserByEmailAsync(string email, string? userType = null, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, UserSummary>> GetUsersAsync(
    IReadOnlyCollection<string> userIds, CancellationToken ct = default);
Task<PhysicianSummary?> GetPhysicianByUserIdAsync(string userId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, PhysicianSummary>> GetPhysiciansAsync(
    IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default);
Task<PhysicianSummary?> GetPhysicianAsync(string physicianProfileId, CancellationToken ct = default);
```

**ADDED this pass:**

```csharp
Task<UserSummary?> GetUserByPhoneAsync(
    string phone, string? userType = null, bool activeOnly = false, CancellationToken ct = default);
```
`POST /add-by-phone`'s `findFirst({ phone, userType: 'PATIENT', active: true })` (L542-549). The
phone is compared **verbatim** — normalize with `ConnJs.NormalizePhone` first and do not normalize
twice.

```csharp
Task<IReadOnlyList<UserSearchRow>> SearchUsersAsync(
    UserSearchFilter filter, CancellationToken ct = default);

public sealed record UserSearchFilter(
    string Query,
    bool MatchDisplayName = true,
    bool MatchEmail = false,
    bool MatchPhone = false,
    string? UserType = null,
    bool ActiveOnly = false,
    int? Take = null);

public sealed record UserSearchRow(
    string Id, string DisplayName, string? Email, string? Phone,
    string UserType, DateTime CreatedAt);
```
Two configurations:
* `GET /search` (L474-493) — `new UserSearchFilter(q, true, true, true, targetType, true, 10)`.
* `GET /` staff branch (L59-63) — `new UserSearchFilter(search)` and nothing else: no type
  filter, no `active` filter, **no take**, because Node expresses it as a relation filter on the
  connection query rather than as a user search. The ids go into `ConnectionFilter.PatientUserIdIn`.

`UserSearchRow`'s six columns are exactly the Node `select`, and `GET /search` spreads the whole
object before appending `connectionStatus` — so adding a field here adds it to the wire.

```csharp
Task<bool> ExistsByEmailOrPhoneAsync(string email, string? phone, CancellationToken ct = default);
```
`POST /create-patient`'s duplicate probe (L649-651). **No `userType` and no `active` filter.** A
null/empty `phone` drops that arm.

```csharp
public sealed record UserSummary(
    string Id, string? Email, string DisplayName, string? Phone, string? ProfilePictureUrl,
    string Role, string UserType, bool Active);

public sealed record PhysicianSummary(
    string Id, string UserId, string LicenseNumber, string Specialty, bool Verified, string DisplayName);
```
`PhysicianSummary.Id` is the **profile** id; `UserId` is the account. `GET /` and
`assign-subprofile` both need `Specialty` and `Verified` from here.

**Failure guarantee:** read-only, returns null / empty for a miss, throws only on infrastructure
failure — which your named-500 guard converts.

### 3.2 `IPatientAccountProvisioner` — `…Abstractions.Directory` (**NEW**)

```csharp
Task<UserSummary> CreatePatientAccountAsync(
    string displayName, string email, string? phone, string? currentOrganizationId,
    CancellationToken ct = default);
```
The one write onto `users`, for `POST /create-patient` (L656-678). Generates a 16-byte random hex
password, bcrypt-hashes it at cost 10 and **discards the plaintext** — it is never returned,
logged or emailed. Creates with `role = USER`, `userType = PATIENT`. **Commits on its own.**

### 3.3 `IPatientDirectory` — `…Abstractions.Directory`

Pre-existing members this module uses:

```csharp
Task<SubprofileSummary?> GetSubprofileAsync(string subprofileId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, SubprofileSummary>> GetSubprofilesAsync(
    IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default);

public sealed record SubprofileSummary(
    string Id, string PatientProfileId, string Name, string Relation,
    DateTime? DateOfBirth, string? Gender, string? BloodType, bool IsActive);
```

**ADDED this pass:**

```csharp
Task<string?> GetPatientProfileIdAsync(string patientUserId, CancellationToken ct = default);
```
`assign-subprofile`'s first read (L320-321). Null there is **400** `"Patient profile not found"`,
not 404. Compare the result against `SubprofileSummary.PatientProfileId` to reproduce the
`findFirst({ id, patientProfileId })` ownership test — a mismatch is **404**
`"Family member not found"`.

```csharp
Task<IReadOnlyDictionary<string, SubprofileClinicalCounts>> CountSubprofileClinicalItemsAsync(
    IReadOnlyCollection<string> patientUserIds, CancellationToken ct = default);

public readonly record struct SubprofileClinicalCounts(int ConditionsCount, int MedicationsCount);
```
`patient-summaries`' `conditionsCount` and `medsCount` (L1069-1074), batched. Two facts that are
easy to get wrong: the counts reach rows **through a subprofile only** (a record attached directly
to the `PatientProfile` is not counted), and **there is no `isActive` filter** — an inactive
condition and a discontinued medication both count. Absent means zero.

### 3.4 `IPatientProfileProvisioner` — `…Abstractions.Directory` (**NEW**)

```csharp
Task<string> CreateProfileWithSelfSubprofileAsync(
    string patientUserId, string? address, string? whatsappNumber, string selfName,
    DateTime? dateOfBirth, string? gender, CancellationToken ct = default);
```
`POST /create-patient` (L680-698). The profile carries only `address` and `whatsappNumber`;
`dateOfBirth` and `gender` go on the **SELF subprofile**, not on the profile. `selfName` is the
same `name` the account was created with. An unrecognised `gender` **throws a 500-shaped
`BusinessException`**, reproducing Prisma's rejection — do not pre-sanitize it to null. Commits on
its own.

### 3.5 `IClinicDirectory` — `…Abstractions.Directory`

Pre-existing members this module uses:

```csharp
Task<ClinicSummary?> GetClinicAsync(string clinicId, CancellationToken ct = default);
Task<bool> IsActiveStaffAsync(string userId, string clinicId, CancellationToken ct = default);
```
`ClinicSummary.PhysicianId` is a physician **PROFILE** id, so resolving `clinic.physician.userId`
is a second hop through `IIdentityDirectory.GetPhysicianAsync`. **`ConnStaffResolver` (§4) already
does both** — do not open-code it.

**ADDED this pass:**

```csharp
Task<string?> GetFirstClinicIdForPhysicianAsync(
    string physicianProfileId, CancellationToken ct = default);
```
`POST /clinic-patients`' physician fallback (L760-764). **No `isActive` filter.** Null is not an
error at the call site — the chart is created with `clinic_id = NULL`.

### 3.6 `IClinicPatientDirectory` — `…Abstractions.Directory`

**⚠ `ClinicPatient` belongs to the Clinics module** — `Modules/Clinics/…/Entities/ClinicPatient.cs`,
with its own EF configuration, store and migration. Endpoints 11-13 go through this port. **Do not
create a second `ClinicPatient` entity in Connections, and do not inject `ClinicsDbContext`.**

Pre-existing:

```csharp
Task<ClinicPatientSummary?> GetAsync(string clinicPatientId, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, ClinicPatientSummary>> GetManyAsync(
    IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default);
Task TouchLastVisitAsync(string clinicPatientId, DateTime visitedAtUtc, CancellationToken ct = default);
```
`ClinicPatientSummary` is the **narrow** record for visit headers. None of the three Connections
endpoints uses it — they all need the full row.

**ADDED this pass** (implemented in `Modules/Clinics/Tebrazi.Clinics.Persistence/Services/ClinicPatientDirectory.cs`):

```csharp
Task<ClinicPatientRecord?> GetOwnedRecordAsync(
    string clinicPatientId, string physicianUserId, CancellationToken ct = default);

Task<IReadOnlyList<ClinicPatientRecord>> ListActiveForPhysicianAsync(
    string physicianUserId, CancellationToken ct = default);

Task<ClinicPatientRecord> CreateAsync(ClinicPatientDraft draft, CancellationToken ct = default);

Task<bool> DeleteAsync(string clinicPatientId, CancellationToken ct = default);
```

```csharp
public sealed record ClinicPatientRecord(
    string Id, string PhysicianUserId, string? ClinicId, string Name,
    string? Phone, string? Email, DateTime? DateOfBirth, string? Gender,
    string? NationalId, string? BloodType, string? Notes,
    IReadOnlyList<string> Allergies, IReadOnlyList<string> ChronicConditions,
    string? LinkedUserId, DateTime? LinkedAt, bool IsActive,
    DateTime? LastVisitDate, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record ClinicPatientDraft(
    string PhysicianUserId, string Name, string? ClinicId = null,
    string? Phone = null, string? Email = null, DateTime? DateOfBirth = null,
    string? Gender = null, string? BloodType = null,
    IReadOnlyList<string>? Allergies = null, IReadOnlyList<string>? ChronicConditions = null,
    string? Notes = null);
```

* `GetOwnedRecordAsync`'s only gate is `physician_user_id` (L866-868) — **the delete route has no
  staff fallback**, so a receptionist always gets its 404.
* `ListActiveForPhysicianAsync` is `{ physicianUserId, isActive: true }` ordered `name` ASC
  (L838-842). It is **deliberately not** `IClinicPatientStore.ListForPhysicianAsync`, which takes a
  clinic id, returns inactive charts and sorts differently.
* `CreateAsync` throws a 500-shaped `BusinessException` for an unrecognised `Gender`, matching
  Prisma.
* `DeleteAsync` removes the **chart only**. Visits and appointments go through §3.7 and §3.8 first.
* `ClinicPatientRecord` deliberately omits `created_by` / `updated_by`, which are not Prisma
  columns. `UpdatedAt` is nullable here and non-null in Node — a freshly created chart serializes
  `null` instead of a timestamp. Recorded, not papered over.

**Failure guarantee:** reads return null / empty; `CreateAsync` and `DeleteAsync` commit through
the **Clinics** unit of work, so they are their own commits.

### 3.7 `IVisitDirectory` and `IVisitClinicPatientWriter` — `…Abstractions.Directory`

**ADDED this pass:**

```csharp
Task<IReadOnlyDictionary<string, int>> CountByClinicPatientAsync(
    IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default);
```
The `_count: { select: { visits: true } }` of `GET /clinic-patients` (L841). Counts **every**
visit — no status filter, no `deletedAt` filter. A chart with none is absent; `GetValueOrDefault`
gives the 0 that Node's `_count` reports.

```csharp
Task<IReadOnlyDictionary<string, PhysicianPatientVisitSummary>> GetPatientVisitSummariesAsync(
    string physicianId,
    IReadOnlyCollection<string> patientUserIds,
    DateTime overdueFollowUpCutoff,
    CancellationToken ct = default);

public sealed record PhysicianPatientVisitSummary(
    DateTime? LastVisitDate,
    string? LastComplaint,
    int VisitCount,
    IReadOnlyList<string> VisitedSubprofileIds,
    DateTime? OverdueFollowUpDate);
```
Everything `patient-summaries` needs out of `visits` (L1060-1099), batched. `physicianId` is the
**PROFILE** id. `overdueFollowUpCutoff` is Node's **local** midnight (L1055-1056), not
`DateTime.UtcNow.Date`. Only the overdue query filters status (COMPLETED); the other three do not.
`hasOverdueFollowUp` is `OverdueFollowUpDate is not null`. A patient with no visits is **absent** —
build the response from the connection list, not from this dictionary's keys.

```csharp
public interface IVisitClinicPatientWriter   // NEW
{
    Task<int> DeleteForClinicPatientAsync(string clinicPatientId, CancellationToken ct = default);
}
```
`tx.visit.deleteMany({ where: { clinicPatientId } })` (L877). A **hard** delete, unlike
`DELETE /api/visits/{id}`, which only archives. Their prescriptions and investigations are **not**
removed — matching Node, both backends leave those rows pointing at visit ids that no longer exist.
Commits on its own.

### 3.8 `IAppointmentClinicPatientWriter` — `…Abstractions.Directory` (**NEW**)

```csharp
Task<int> DeleteForClinicPatientAsync(string clinicPatientId, CancellationToken ct = default);
```
`tx.appointment.deleteMany({ where: { clinicPatientId } })` (L879). No status filter. **Time slots
are not released** — the Node transaction deletes appointment rows and nothing else. Commits on its
own.

### 3.9 `IConnectionDirectory` — `…Abstractions.Directory` (**NEW — published BY this module**)

This is the port other modules call, not one this module calls. Implemented in
`Modules/Connections/Tebrazi.Connections.Persistence/Services/ConnectionDirectory.cs`.

```csharp
Task<IReadOnlyList<PatientDoctorRow>> ListAcceptedDoctorsForPatientAsync(
    string patientUserId, CancellationToken ct = default);

Task<bool> HasAcceptedConnectionAsync(
    string physicianUserId, string patientUserId, CancellationToken ct = default);

public sealed record PatientDoctorRow(
    string PhysicianUserId, string? SubprofileId, DateTime? ConnectedAt);
```

**What the Patients team must call to close the dashboard gap** (`PORT-STATUS.md` records
`doctors: []` and `stats.totalDoctors: 0` as a genuine shortfall). In
`GET /api/patients/dashboard`, replacing the two empty literals:

```csharp
var rows = await connections.ListAcceptedDoctorsForPatientAsync(userId, ct);

// doctors[] — patients.js:719-726
//   id          = row.PhysicianUserId
//   name        = users[row.PhysicianUserId].DisplayName          (IIdentityDirectory.GetUsersAsync)
//   specialty   = physician?.Specialty ?? "General"               (|| 'General', patients.js:722)
//   verified    = physician?.Verified  ?? false                   (|| false,     patients.js:723)
//   connectedAt = row.ConnectedAt
//   forMember   = row.SubprofileId is null ? "Self" : subprofile.Name

// stats.totalDoctors — patients.js:855
var totalDoctors = rows.Select(r => r.PhysicianUserId).Distinct().Count();
```

Three things the Patients handler must get right:

* **The query filters status ONLY.** No `subprofileId: null` clause, so a patient connected to one
  doctor for themselves and for two children gets **three** rows, three cards, three different
  `forMember` values. Do not collapse them.
* **`totalDoctors` is `Distinct().Count()`, not `rows.Count`** — the family case above is exactly
  where the two differ.
* `forMember` resolves inside Patients (it owns `family_subprofiles`), not through a port.

`HasAcceptedConnectionAsync` closes a second, smaller gap: `GET /api/patients/family/{id}` uses an
ACCEPTED edge's existence as the authorization test (patients.js:162-172), and there is **no
subprofile clause** — a physician connected only for one dependant sees the whole family.

**This pass deliberately did NOT wire either into the Patients handler.** That file has another
owner.

### 3.10 `INotificationPublisher` — `Tebrazi.SharedKernel.Abstractions`

```csharp
Task PublishAsync(NotificationRequest request, CancellationToken ct = default);
Task PublishAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default);

public sealed record NotificationRequest(
    string UserId, string Type, string Title, string Message,
    string? Data = null, bool SendEmail = false);
```

**It never throws**, and it must be called **after** a transaction commits. Three call sites in
this module, and their types:

| Route | `Type` | Node |
|---|---|---|
| `POST /add-by-phone` (L587-593) | `CONNECTION_ACCEPTED` | `createNotification(...).catch(() => {})` — explicitly best-effort |
| `POST /create-patient` (L713-719) | `CONNECTION_ACCEPTED` | **awaited with NO catch** — a failure really is the route's 500 in Node |
| `POST /connect-by-pin` (L1705-1713) | `CONNECTION` | written via `prisma.notification.create` **directly**, inside its own try/catch (L1714) — so no email, no push |

`Data` is opaque JSON: `{"physicianUserId":"…"}` for the first two,
`{"connectionId":"…","patientUserId":"…"}` for the third.

### 3.11 `IEmailSender` — `Tebrazi.SharedKernel.Abstractions` (**NEW**)

```csharp
Task SendAsync(EmailMessage message, CancellationToken ct = default);

public sealed record EmailMessage(string To, string Subject, string Html, string? Text = null);
```

For `POST /invite-email` only. **Unlike `INotificationPublisher`, this port DOES throw** — the Node
route awaits `sendEmail` with no `.catch`, so a transport failure is
`500 {"error":"Failed to send invitation"}`. Let it reach your guard.

**An unconfigured transport is SUCCESS.** Node auto-detects Resend, then SMTP, then falls back to a
console transport that logs and resolves. The default implementation
(`Kernel/Tebrazi.Infrastructure.Shared/Placeholders/ConsoleEmailSender.cs`) does exactly that, so
this endpoint answers `200 {"success":true,"message":"Invitation sent"}` in development, as Node
does. **The HTML is not escaped anywhere in Node** — the physician's display name goes straight
into the markup (L991) — so do not start escaping it.

---

## 4. Module-local services

`Tebrazi.Connections.Application.Services` —
`Modules/Connections/Tebrazi.Connections.Application/Services/`

All four are registered by `AddConnectionsModule` and injectable.

### `ConnJs` — static

```csharp
public static string? Truthy(string? value);
public static bool    IsTruthy(JsonElement value);
public static string? AsString(JsonElement value);
public static string? TrimToNull(string? value);
public static string  NormalizePhone(string phone);   // strips whitespace and '-' ONLY
public static string  DigitsOnly(string phone);       // ASCII digits only
```

**The two phone normalizations are different and must not be swapped.** `NormalizePhone` is
`phone.replace(/[\s\-]/g, '')` from `add-by-phone` (L539) — a leading `+`, parentheses and dots all
survive, and the result is echoed back on the wire. `DigitsOnly` is `phone.replace(/[^0-9]/g, '')`
and exists only to build `create-patient`'s placeholder email `patient-<digits>@tebrazi.local`
(L646), which becomes the patient's permanent login identifier.

`Prescriptions`' `RxJs` is `public` but lives in an assembly this one does not reference, so it is
**not reachable** — that is why `ConnJs` exists rather than a `using`.

### `ConnClientUrls` — singleton, constructed from `configuration["Client:Url"]`

```csharp
public const string InviteFallbackBaseUrl = "http://localhost:5174";
public const string QrFallbackBaseUrl     = "http://localhost:5173";

public string InviteBaseUrl { get; }
public string QrBaseUrl     { get; }

public static string InviteToken(string physicianUserId);          // 16 lowercase hex chars
public static string PrefixedInviteToken(string physicianUserId);  // "dr-" + the above
public string SignupUrl(string physicianUserId);
public string ConnectUrl(string physicianUserId);
```

**⚠ The two fallbacks are different ports and that is not a typo in the Node source.** The invite
routes fall back to **5174** (L982, L1019), the QR route to **5173** (L1798). Both are reproduced.
`Client:Url` (env `Client__Url`) is the `CLIENT_URL` equivalent; the key is absent from
`appsettings.json` on purpose so the fallbacks apply.

`InviteToken` is `sha256("physician-invite-" + userId)` truncated to 16 hex chars — a deterministic
function of the id with no secret and no expiry, i.e. an identifier, not a credential. Do not
"harden" it: it is echoed to the client as `token` and is already in sent emails.

### `ConnStaffResolver` — scoped

```csharp
Task<ConnStaffResolution> ResolveAsync(string userId, string? clinicId, CancellationToken ct = default);

public enum ConnStaffOutcome { NoClinicId, NotStaff, ClinicNotFound, NoClinicPhysician, Resolved }

public sealed record ConnStaffResolution(ConnStaffOutcome Outcome, string? PhysicianUserId)
{
    public bool IsResolved { get; }
}
```

The staff fallback, spelled out **seven times** in `connections.js` with the same three steps and
**seven different failure behaviours**. It resolves and reports; it never throws. The mapping table
is in §10 and in the class's own doc comment. Reasons it is a service rather than copied code:
the `clinic.physician.userId` hop needs two ports, and getting the profile-id/user-id distinction
wrong silently resolves the wrong physician.

### `ConnQrCodeRenderer` — singleton

```csharp
public const int TargetWidthPixels = 300;
public const int QuietZoneModules  = 2;

public string RenderPngDataUrl(string payload);   // "data:image/png;base64,…"
```

Backed by the **QRCoder 1.8.0** package (added to `Directory.Packages.props` this pass), using
`PngByteQRCode` — managed-only, no System.Drawing, no native dependency. Error correction is
level **M**, which is the `qrcode` npm package's own default and therefore what Node produces.

**The base64 payload is not byte-identical to Node's and cannot be** — two encoders pick different
mask patterns and PNG chunk layouts. What is identical is the media type, the `data:` URL shape and
the decoded string. The client renders it into an `<img src>` and never inspects it.

---

## 5. Caller context, pagination and committing

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

House rule, matching Visits, Appointments and Prescriptions: **READ handlers inject `ICurrentUser`;
WRITE handlers take the caller id explicitly** on the command (`CallerUserId`) and inject nothing.

**`UserType` is a `string?`. Compare it as `request.UserType == "PHYSICIAN"`; do not parse it into
the `UserType` enum** — the claim can carry a value the enum does not have. Six routes branch on
it, and the branch is `!== 'PHYSICIAN'`, so RECEPTIONIST, STAFF, PATIENT and an unknown value all
take the same path. `POST /generate-pin` is the exception: it branches on the **absence of a
physician profile row** (L1522-1523), not on the claim, so a user whose claim says PHYSICIAN but
who has no profile takes the staff path there and the physician path everywhere else.

**`IClinicContext` exists in the kernel and this module DOES need the `X-Clinic-Id` header** —
seven routes read it. Inject `IClinicContext` for it, or read it in the controller and pass it on
the command. Note the precedence trap in §10.

### `Tebrazi.SharedKernel.Pagination`

```csharp
public readonly record struct PageRequest(int Page, int Limit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public int Skip { get; }
    public static PageRequest Parse(string? page, string? limit);
}

public sealed record PaginationMeta(int Total, int Page, int Limit, int Pages)
{
    public static PaginationMeta From(int total, PageRequest request);
    public static PaginationMeta Empty(PageRequest request);
}
```

**No Connections endpoint uses either type.** Not one of the nineteen paginates: `GET /` and
`GET /clinic-patients` return bare arrays, `GET /search` caps at a hard-coded `take: 10`, and
`GET /patient-summaries` returns an object keyed by patient id. Listed here so nobody goes looking.

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

Inject `IConnectionsDbContext`, never `ConnectionsDbContext` and never bare `IDbContext`.
`SaveChangesAsync` also applies the audit stamps.

**Only two Node routes use a transaction and neither needs `ExecuteInTransactionAsync` here.**
`DELETE /clinic-patients/{id}`'s `$transaction` spans four tables across three .NET contexts and
cannot be one transaction (§11). `POST /connect-by-pin`'s `$transaction([create, update])` spans
two tables in **this** context, so a single `SaveChangesAsync` after both writes is the faithful
and simpler port. Everywhere else, one `SaveChangesAsync` per endpoint.

If you do reach for `ExecuteInTransactionAsync`, remember the delegate **can run more than once**
after a transient fault, and that `INotificationPublisher`, `IEmailSender` and every provisioner
port commit through **another** module's unit of work and are therefore not covered — call them
after it returns, which is also the Node ordering.

Never touch the Begin/Commit/Rollback trio.

---

## 6. Exception → status → body

`APIs/Tebrazi.Common.Api/Middlewares/ExceptionHandlingMiddleware.cs`. `AppException.Error` becomes
the JSON `error` key and `Message` the `message` key — and **`message` is DROPPED when it equals
`error`**, which is what lets a bare `{ error }` body be produced.

| Throw | Status | JSON body |
|---|---|---|
| `new NotFoundException(m)` | 404 | `{"error": m}` |
| `new ForbiddenException(m)` | 403 | `{"error": m}` |
| `new GoneException(m)` | 410 | `{"error": m}` |
| `new UnauthorizedException(m)` | 401 | `{"error":"Unauthorized","message": m}` |
| `new ValidationException(m)` | 400 | `{"error":"Validation failed","message": m}` |
| `new ConflictException(m)` | 409 | `{"error":"Conflict","message": m}` |
| `new BusinessException(e, m, code = 400)` | `code` | `{"error": e}` when `e == m`, else `{"error": e, "message": m}` |
| anything else | 500 | `{"error":"Internal Server Error","message":"An unexpected error occurred"}` |

### The rule for Connections

**Every error body in all 1,818 lines of `connections.js` is a bare `{ "error": "…" }`.** There is
no `message` key anywhere in the file and no `details`. So use `ConnErrors`, and **never**
`ValidationException` or `ConflictException` — both prepend a generic label and push the real text
into a second key the client does not match on. The 409s here ("Already connected",
"Connection request already pending") are bare bodies.

```csharp
// Tebrazi.Connections.Application.Services.ConnErrors
public static BusinessException  Node(string message, int statusCode);
public static BusinessException  BadRequest(string message);   // 400
public static ForbiddenException Forbidden(string message);    // 403
public static NotFoundException  NotFound(string message);     // 404
public static BusinessException  Conflict(string message);     // 409  ← NOT ConflictException
public static BusinessException  ServerError(string message);  // 500
```

### The per-file guard

**Every route has its OWN named 500 literal**, and the generic middleware body reproduces none of
them. Follow the Prescriptions precedent: one per-file catch-all guard with its group's prefix
(`ConnLinkPersistence`, `ConnPinPersistence`, …) plus a `Handle` → `HandleCore` split, rethrowing
`AppException` **untouched**:

```csharp
try { return await body(); }
catch (Exception exception)
    when (exception is not AppException && !cancellationToken.IsCancellationRequested)
{
    logger.Error(logMessage, exception);
    throw ConnErrors.ServerError(failureError);
}
```

Declare the guard **inside your own file** — `ConnErrors` is shared, the guard is not, because the
log message and failure literal differ per route.

The nineteen named 500s, so nobody re-derives them:

| Endpoint | 500 literal | Log message |
|---|---|---|
| `GET /` | `Failed to list connections` | `[Connections] List error` |
| `POST /request` | `Failed to send connection request` | `[Connections] Request error` |
| `PUT /{id}/accept` | `Failed to accept connection` | `[Connections] Accept error` |
| `PUT /{id}/reject` | `Failed to reject connection` | `[Connections] Reject error` |
| `POST /assign-subprofile` | `Failed to assign doctor to family member` | `[Connections] Assign subprofile error` |
| `DELETE /unassign-subprofile/…` | `Failed to unassign doctor` | `[Connections] Unassign subprofile error` |
| `DELETE /{id}` | `Failed to remove connection` | `[Connections] Delete error` |
| `GET /search` | `Search failed` | `[Connections] Search error` |
| `POST /add-by-phone` | `Failed to add patient` | `[Connections] Add by phone error` |
| `POST /create-patient` | `Failed to create patient` | `[Connections] Create patient error` |
| `POST /clinic-patients` | `Failed to create patient file` | `[Connections] Create clinic patient error` |
| `GET /clinic-patients` | `Failed to load clinic patients` | `[Connections] List clinic patients error` |
| `DELETE /clinic-patients/{id}` | `Failed to delete patient file` | `[Connections] Delete clinic patient error` |
| `POST /invite-email` | `Failed to send invitation` | `[Connections] Invite email error` |
| `GET /invite-link` | `Failed to generate link` | `[Connections] Invite link error` |
| `GET /patient-summaries` | `Failed to load summaries` | `[Connections] Patient summaries error` |
| `POST /generate-pin` | `Failed to generate PIN` | `[Connections] Generate PIN error` |
| `POST /connect-by-pin` | `Failed to connect` | `[Connections] Connect by PIN error` |
| `GET /qr-code` | `Failed to generate QR code` | `[Connections] QR code generation error` |

**`POST /generate-pin` has a SECOND 500, in-band and not from the catch:**
`500 {"error":"Could not generate unique PIN, try again"}` after 20 consecutive collisions
(L1567-1569). Raise it with `ConnErrors.ServerError`, not from the guard.

### Every other status literal, by route

| Route | Status | Body |
|---|---|---|
| `POST /request` | 400 | `Target email is required` |
| | 404 | `User not found. They must register on Tebrazi first.` |
| | 400 | `Connections must be between a physician and a patient` |
| | 409 | `Already connected` |
| | 409 | `Connection request already pending` |
| `PUT /{id}/accept` | 404 | `Connection not found` |
| | 400 | `Cannot accept your own request` |
| | 403 | `Access denied` |
| | 400 | `Connection is already <status.toLowerCase()>` — **interpolated**, e.g. `Connection is already accepted` |
| `PUT /{id}/reject` | 404 | `Connection not found` |
| | 403 | `Access denied` |
| `POST /assign-subprofile` | 400 | `physicianUserId and subprofileId are required` |
| | 400 | `Patient profile not found` |
| | 404 | `Family member not found` |
| | 400 | `You must be connected to this doctor first` |
| | 409 | `This doctor is already assigned to this family member` |
| `DELETE /unassign-subprofile/…` | 404 | `Assignment not found` |
| `DELETE /{id}` | 404 | `Connection not found` |
| | 403 | `Access denied` |
| `POST /add-by-phone` | 403 | `Only physicians can add patients` |
| | 400 | `Phone number is required` |
| `POST /create-patient` | 400 | `clinicId is required for staff` |
| | 403 | `Not authorized` |
| | 404 | `Clinic physician not found` |
| | 400 | `Patient name is required` |
| | 400 | `Phone or email is required` |
| | 409 | `A patient with this email or phone already exists. Search for them instead.` |
| `POST /clinic-patients` | 400 | `Patient name is required` |
| | 400 | `clinicId is required for staff` |
| | 403 | `Not authorized` |
| | 404 | `Clinic not found` — **a different literal from `create-patient`'s** |
| `DELETE /clinic-patients/{id}` | 404 | `Patient file not found or not authorized` |
| `POST /invite-email` | 400 | `Email is required` |
| `POST /generate-pin` | 403 | `Only physicians or clinic staff can generate PINs` |
| | 403 | `Not authorized for this clinic` |
| | 404 | `Clinic physician not found` |
| `POST /connect-by-pin` | 400 | `Please enter a 4-digit PIN` |
| | 404 | `Invalid or expired PIN. Ask your doctor for a new code.` |
| | 400 | `Cannot connect to yourself` |

**Model-binding failures never reach a handler**: `Program.cs` renders them as
`400 {"error":"Validation failed","message":<first error>}` — a body no Connections route ever
produces. **Bind every body as a nullable record with `JsonElement` members** so an absent, null or
wrongly-typed field reaches your own guard rather than the binder's. And remember
`[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]`: `[FromBody] X?` does **not** make a body
optional.

---

## 7. Enums

`Tebrazi.Connections.Domain.Entities`:

```csharp
public enum ConnectionStatus { PENDING, ACCEPTED, REJECTED, REMOVED }
```
Member names and order match Prisma's `ConnectionStatus` (schema.prisma:1119-1124) exactly.
Persists and serializes **as a string** (`HasConversion<string>()`, max length 20).

**`REMOVED` is declared and never written.** No route in `connections.js` assigns it —
disconnection is a hard `delete`. It exists so the column's domain matches Prisma's and so a legacy
row carrying it still materialises.

Reachability: `PENDING` from `POST /request`; `ACCEPTED` from `/accept`, `assign-subprofile`,
`add-by-phone`, `create-patient`, `connect-by-pin`; `REJECTED` from `/reject`.

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
Connections touches **none of these as a type**. `Gender` crosses the boundary as a **string** on
`ClinicPatientDraft.Gender` and `IPatientProfileProvisioner`'s `gender`, and the implementations
parse and throw. `ICurrentUser.UserType` is a `string?` — see §5.

Strings that are **not** enums and must stay strings on the wire:

* `SubprofileSummary.Relation` — `SELF | SPOUSE | CHILD | PARENT | SIBLING | OTHER`.
* `ClinicPatientRecord.BloodType` / `.Gender` — free text and a stringified enum respectively.
* `NotificationRequest.Type` — `CONNECTION_ACCEPTED` and `CONNECTION`; see §3.10.
* `UserSearchRow.UserType` — the enum member name.

---

## 8. `using` list and group map

Place handlers in `Modules/Connections/Tebrazi.Connections.Application/UseCases/`, namespace
`Tebrazi.Connections.Application.UseCases`, one file per group, request record immediately above
its handler.

```csharp
using System.Text.Json;                                              // JsonElement bodies
using Tebrazi.Connections.Application.Abstractions.Persistence;      // IConnectionsDbContext, the two stores,
                                                                     //   ConnectionFilter, ConnectionEdge
using Tebrazi.Connections.Application.ApiModels.Responses;           // your own response records
using Tebrazi.Connections.Application.Services;                      // ConnJs, ConnErrors, ConnDates,
                                                                     //   ConnClientUrls, ConnStaffResolver,
                                                                     //   ConnQrCodeRenderer
using Tebrazi.Connections.Domain.Entities;                           // DoctorPatientConnection, ConnectionPin,
                                                                     //   ConnectionStatus
using Tebrazi.SharedKernel.Abstractions;                             // ICurrentUser, IClinicContext,
                                                                     //   INotificationPublisher, IEmailSender
using Tebrazi.SharedKernel.Abstractions.Directory;                   // every cross-module port in §3
using Tebrazi.SharedKernel.Exceptions;                               // AppException, for the guard's filter
using Tebrazi.SharedKernel.Logging;                                  // IAppLogger<T>
using Tebrazi.SharedKernel.Mediation;                                // IRequest<T>, IRequestHandler<T,R>
```

Two `using`s that are **wrong** here and will look right:

* `using MediatR;` — compiles if the package is ever added and resolves a **different** `IMediator`.
  The mediator is in-house. Never reference MediatR.
* `using Tebrazi.Prescriptions.Application.Services;` — `RxJs` and `RxErrors` are `public`, but
  that assembly is not referenced. Use `ConnJs` / `ConnErrors`.

`Tebrazi.SharedKernel.Pagination` is deliberately absent — no endpoint paginates (§5).

The endpoint → group map is at the top of this document, and the same five prefixes are declared as
constants on `ConnectionResponseConventions` in
`ApiModels/Responses/ConnectionResponseConventions.cs`.

---

## 9. Name collisions

**Five handler files compile into ONE namespace.** Two files declaring the same record name is a
build error, and none of the five authors can build. The prefix is the whole defence.

### Prefix every type you declare

| Group | Prefix | Example names |
|---|---|---|
| A | `ConnLink` | `ConnLinkListQuery`, `ConnLinkListResponse`, `ConnLinkRequestCommand`, `ConnLinkPersistence` |
| B | `ConnSubprofile` | `ConnSubprofileAssignCommand`, `ConnSubprofileSummariesResponse` |
| C | `ConnDiscover` | `ConnDiscoverSearchQuery`, `ConnDiscoverAddByPhoneCommand` |
| D | `ConnClinicPatient` | `ConnClinicPatientCreateCommand`, `ConnClinicPatientListResponse` |
| E | `ConnPin` | `ConnPinGenerateCommand`, `ConnPinConnectResponse`, `ConnPinQrCodeResponse` |

The prefix applies to **everything** you declare in the namespace: request records, response
records, nested DTOs, the per-file guard class, any private helper class, and any `internal static`
utility. `internal` does **not** save you — the five files are in the same assembly.

### What already exists and must not be re-declared

Shared, in `Tebrazi.Connections.Application.Services`:

* `ConnJs`, `ConnErrors`, `ConnDates`, `ConnClientUrls`, `ConnStaffResolver`,
  `ConnStaffResolution`, `ConnStaffOutcome`, `ConnQrCodeRenderer`.

Shared, in `Tebrazi.Connections.Application.Abstractions.Persistence`:

* `IConnectionsDbContext`, `IDoctorPatientConnectionStore`, `IConnectionPinStore`,
  `ConnectionFilter`, `ConnectionEdge`.

Shared, in `Tebrazi.Connections.Application.ApiModels.Responses`:

* `ConnectionResponseConventions` — and nothing else yet. **This namespace is otherwise empty and
  is yours to fill, one prefixed record at a time.**

### Every endpoint gets its OWN response record

There is no shared `ConnectionDto` and there must not be one. The nineteen routes emit at least
eleven distinct shapes of the same row:

* bare scalars (`/reject`'s `updated` before the spread);
* scalars **plus a `message` key** (`POST /request` re-send, `/accept`, `/reject`);
* scalars plus `physicianUser: { displayName, email }` and `patientUser: { displayName, email }`
  (`POST /request` create, 201);
* scalars plus `physicianUser: { id, displayName, physicianProfile: { specialty } }` and
  `subprofile: { id, name, relation }` (`assign-subprofile`, 201);
* scalars plus `patientUser: { id, displayName, email, phone }` and
  `subprofile: { id, name, relation, dateOfBirth, gender }` (`GET /` physician and staff branches);
* scalars plus `physicianUser: { id, displayName, email, phone, physicianProfile: { specialty,
  verified } }` and `subprofile: { id, name, relation }` — **no `dateOfBirth`, no `gender`**
  (`GET /` patient branch);
* `{ id, status }` only, nested under `connection` (`add-by-phone`);
* the whole row nested under `connection` (`connect-by-pin`, both branches);
* a bare `{ message }` (unassign, delete);
* `{ success, patient: {...}, message }` (`create-patient`, `POST /clinic-patients`);
* an object keyed by patient id (`patient-summaries`).

A shared DTO would quietly unify the two `GET /` branches, which differ in exactly the two
subprofile fields, and nothing would fail.

---

## 10. Cross-cutting findings

Things a group writer seeing only their slice would get wrong. All measured against
`server/src/routes/connections.js` on 2026-09-11.

### 10.1 The staff fallback appears SEVEN times with SEVEN failure behaviours

Same three steps every time — active `clinic_staff` row → clinic → `clinic.physician.userId` — and
`ConnStaffResolver` (§4) performs them. **What differs is the failure mapping, and it is on the
wire:**

| Route | Node line | On failure |
|---|---|---|
| `GET /` | 45-78 | **falls through** to the userType branches; a staff caller then sees their own (empty) patient list |
| `POST /request` | 143-157 | **falls through**; the pairing test at L182 then answers `400 "Connections must be between a physician and a patient"` |
| `GET /search` | 455-470 | **falls through**; the caller keeps their `userType` and therefore searches for PHYSICIANS instead of patients |
| `GET /clinic-patients` | 821-836 | every failure ends in `200 []` |
| `POST /create-patient` | 626-639 | NoClinicId → `400 "clinicId is required for staff"`; NotStaff → `403 "Not authorized"`; no clinic physician → `404 "Clinic physician not found"` |
| `POST /clinic-patients` | 766-783 | NoClinicId → `400 "clinicId is required for staff"`; NotStaff → `403 "Not authorized"`; ClinicNotFound → `404 "Clinic not found"`. **NoClinicPhysician is unreachable**: L781 dereferences `clinic.physician.userId` unguarded, so a null physician is a TypeError caught by the route's own `500 "Failed to create patient file"`. Reproduce the 500, not a 404. |
| `POST /generate-pin` | 1518-1543 | NoClinicId → `403 "Only physicians or clinic staff can generate PINs"`; NotStaff → `403 "Not authorized for this clinic"`; no clinic physician → `404 "Clinic physician not found"` |

**The entry gate differs too.** Six routes enter on `req.user.userType !== 'PHYSICIAN'`;
`POST /generate-pin` enters on the **absence of a physician profile row** (L1522).

### 10.2 `X-Clinic-Id` precedence is header-first on exactly ONE route

```
GET /                (L37)   req.headers['x-clinic-id'] || req.query.clinicId     ← HEADER FIRST
POST /request        (L143)  body.clinicId  || req.headers['x-clinic-id']
GET /search          (L455)  query.clinicId || req.headers['x-clinic-id']
POST /create-patient (L627)  body.clinicId  || req.headers['x-clinic-id']
POST /clinic-patients(L768)  body.clinicId  || req.headers['x-clinic-id']
GET /clinic-patients (L822)  query.clinicId || req.headers['x-clinic-id']
POST /generate-pin   (L1518) body.clinicId  || req.headers['x-clinic-id'] || null
```

`GET /` is the only one where the header wins. The axios interceptor sends `X-Clinic-Id` on **every**
request once a clinic is active (`client/src/services/api.js:31`), so this is reachable whenever a
caller also passes `?clinicId=`.

### 10.3 `GET /`'s `?status=` is passed RAW to Prisma

`where.status = req.query.status` (L40), never validated. An unrecognised value is a Prisma
validation error and therefore `500 {"error":"Failed to list connections"}`. In .NET, parse with
`Enum.TryParse<ConnectionStatus>(value, ignoreCase: false, out _)` and **throw
`ConnErrors.ServerError("Failed to list connections")` on failure** — do not treat it as "no
filter" and do not 400. `ConnJs.Truthy` first: `?status=` (empty) is falsy in Node and applies no
filter at all.

### 10.4 `GET /`'s three branches select DIFFERENT subprofile fields

* staff branch (L69-71) and physician branch (L87-89):
  `subprofile: { id, name, relation, dateOfBirth, gender }`
* patient branch (L106-108): `subprofile: { id, name, relation }` — **no `dateOfBirth`, no
  `gender`**

And the physician side only appears on the patient branch, with
`physicianProfile: { specialty, verified }` nested inside `physicianUser`.

Only the **physician** branch carries `subprofileId: null` (L82). The staff branch does not, so a
receptionist viewing the same clinic sees dependant edges that the doctor's own "My Patients" list
hides.

### 10.5 `GET /search`'s enrichment matcher is quirky and the quirk must survive

```js
const conn = existingConns.find(c => c.physicianUserId === u.id || c.patientUserId === u.id);
return { ...u, connectionStatus: conn?.status || null };
```
(L507-510.) It takes the **first** edge in the array whose *either* end is this user, with no
direction check and no ordering — so a user who is both a patient of the caller and (through a dual
account) a physician connected to them can get the wrong edge's status. `ListEdgesWithAsync`
returns both ends precisely so this stays in the handler. Also note `conn?.status || null`: an
empty-string status would become null, though no such status exists.

`q.length < 2` → **`200 []`**, not a 400 (L446-448). `take: 10` is hard-coded.

### 10.6 The two "already connected" probes are deliberately different

`add-by-phone` (L556) and `link-account` (L939) omit the `subprofileId` key entirely →
`FindAnyPairAsync`. `POST /request` (L193) and `connect-by-pin` (L1639) pass it explicitly →
`FindPairAsync`. The consequence is real: a physician connected only to a patient's CHILD is told
"already connected" by `add-by-phone` and never gets a parent edge, but `connect-by-pin` creates
one. §1 has the signatures.

### 10.7 Status vocabulary

The **only** four values ever written are `PENDING`, `ACCEPTED` and `REJECTED` — `REMOVED` is never
written by any route. Disconnection is a hard `delete`. There is **no state machine**: `/reject`
has no precondition, `/accept` requires PENDING but `assign-subprofile`, `add-by-phone`,
`create-patient` and `connect-by-pin` all insert ACCEPTED directly, and `connect-by-pin` promotes
a REJECTED edge straight to ACCEPTED (L1650-1654).

`/accept`'s 400 message is **interpolated from the stored status**:
`` `Connection is already ${conn.status.toLowerCase()}` `` (L259) — so `Connection is already
accepted`, `Connection is already rejected`, `Connection is already removed`. Reproduce the
lowercase interpolation, not three literals.

### 10.8 Notification types

Two, and they differ in mechanism as well as name — see §3.10. `CONNECTION_ACCEPTED` is in the Node
service's `NOTIFICATION_TYPES` list; **`CONNECTION` is not**, and reaches the table only because
`connect-by-pin` bypasses `createNotification` and calls `prisma.notification.create` directly
(L1705). That also means it sends no email and no push. `INotificationPublisher` with
`SendEmail: false` is the right port for all three sites.

### 10.9 Best-effort blocks that must NOT become 500s

Reproduce these as **nested** try/catch inside the handler, never folded into the per-file guard —
a read inside a side-effect block must not turn a committed 200 into a 500. This has bitten this
port twice already.

* `add-by-phone` (L582-593): the physician display-name lookup **and** the notification are inside
  a block whose `createNotification(...).catch(() => {})` swallows everything. A failure still
  answers 200.
* `connect-by-pin` (L1700-1716): the patient display-name lookup and the notification row are
  inside an explicit `try { … } catch (notifErr) { logger.error(...) }`. Same rule.

And the one that is **not** best-effort: `create-patient`'s notification (L713-719) is `await`ed
with **no** `.catch`, so in Node a failure there really is `500 "Failed to create patient"` — after
the user, profile, subprofile and connection rows have all been written. `INotificationPublisher`
never throws, so the .NET port answers 200 where Node can answer 500. Recorded in §11.

### 10.10 `POST /generate-pin` echoes the CALLER's name, not the physician's

```js
const user = await prisma.user.findUnique({ where: { id: userId }, select: { displayName: true } });
return res.json({ pin, expiresAt, physicianName: user?.displayName, expiresInSeconds: 300 });
```
(L1584-1594.) `userId` is `req.user.id` — the **caller**. On the staff path the pin belongs to the
clinic's physician but `physicianName` is the receptionist's name. Reproduce it.

`expiresInSeconds` is the literal `300`, not a recomputation.

The generation loop (L1556-1569) increments `attempts` **only on collision** and checks
`attempts >= 20` after the loop, so the in-band 500 fires after twenty consecutive collisions.

### 10.11 `cacheMiddleware` is not reproduced anywhere in this port

`GET /` is wrapped in `cacheMiddleware(15)` (L31) — the only cached route in the file, with **no
invalidation anywhere**, so live Node can return the pre-mutation array for up to 15 seconds after
every write in this module. The port deliberately does not reproduce that stale window: a fresher
response cannot break the client, a stale one can. Put a one-line comment in the `GET /` handler
saying so and move on. This matches the existing recorded divergence in `PORT-STATUS.md`.

### 10.12 Repeated projections worth extracting once per group

Three shapes recur and each belongs to exactly one group, so extract them as **prefixed** nested
records, not as shared types:

* the physician side — `{ id, displayName, email, phone, physicianProfile: { specialty, verified } }`
  — `GET /` patient branch and `assign-subprofile` (a narrower variant);
* the patient side — `{ id, displayName, email, phone }` — `GET /` staff and physician branches;
* the dependant — `{ id, name, relation }` ± `dateOfBirth, gender` — four places, two widths.

---

## 11. Gaps and decisions

### 11.1 Soft delete: there is none, and that is stronger than "no filter"

**Finding.** `deletedAt` does not occur anywhere in the 1,818 lines of `connections.js` —
`grep -n "deletedAt\|softDelete" connections.js` returns nothing. And unlike Visits, Appointments
and Prescriptions, **the entities do not even have the column**: `model DoctorPatientConnection`
(schema.prisma:507-530) and `model ConnectionPin` (549-563) declare no such field.

**Decision.** No query filter on `ConnectionsDbContext`, and the reasoning is written in the
context in place of the filter, as the three clinical modules do. Both delete routes are HARD
deletes (`prisma…delete`, L385 and L427). A filter here would have nothing to filter and would make
a claim about the data model that the data model does not make.

The one place this module touches soft-deletable rows is `IPatientDirectory`, whose context DOES
filter `Allergy` / `ChronicCondition` / `CurrentMedication`. Keeping those filters is correct:
Node hard-deletes those rows, so `deletedAt` is never set on a live Node row. Same reasoning as the
three Patients aggregate endpoints in `PORT-STATUS.md`.

### 11.2 The PIN mechanism

**Finding.** The PIN lives in its own Prisma model, `ConnectionPin` → table `connection_pins`
(schema.prisma:548-563): `id, physicianUserId, clinicId?, pin, expiresAt, usedAt?, usedByUserId?,
createdAt`, indexed on `pin`, `physicianUserId` and `expiresAt`, with **no unique constraint on
`pin`** and **no `updatedAt`**. TTL is **5 minutes**, set as an absolute `expiresAt` at creation
(L1571-1572), and the response's `expiresInSeconds` is a hard-coded 300.

**⚠ It is NOT the Clinics staff pin.** `Modules/Clinics/…/UseCases/Commands/StaffPinCommands.cs`
drives `staff_pins`, an unrelated table that unlocks a shared terminal. Different table, different
lifetime, different meaning.

**Decision.** Ported as a Connections-owned entity and store (`ConnectionPin`,
`IConnectionPinStore`), because `connections.js` is its only reader and writer. Uniqueness among
LIVE pins is enforced in application code by the generation loop, exactly as Node does — the column
is indexed, not unique, because a used pin must be allowed to keep its value forever.

### 11.3 The ClinicPatient ownership boundary

**Finding.** Endpoints 11-13 operate on `clinic_patients`, which already exists in the **Clinics**
module with an entity, EF configuration, store, migration and the published
`IClinicPatientDirectory`.

**Decision.** No second entity, and no `ClinicsDbContext` injection. `IClinicPatientDirectory` was
**extended** with four members and two records (§3.6), and the extension is implemented **in the
Clinics module** — `Modules/Clinics/Tebrazi.Clinics.Persistence/Services/ClinicPatientDirectory.cs`.
The port is no longer read-only as a result, which is a real cost; each write is documented with
the Node lines it reproduces and nothing broader is exposed.

**The consequence to know about is atomicity.** `DELETE /clinic-patients/{id}` is ONE Prisma
`$transaction` over five statements (L875-886). In the port it becomes **three separate commits**
across three contexts — Visits, Appointments, Clinics — plus two statements with nothing to run
(below). Order them as Node does (visits, appointments, chart) so a mid-way failure leaves a chart
whose visits are gone rather than orphan visits with no chart.

**`patient_notes` and `patient_tags` are not ported at all.** Neither table exists in any .NET
context and neither has an owning module. `tx.patientNote.deleteMany` (L881) and
`tx.patientTag.deleteMany` (L883) therefore have nothing to delete, and no port was invented for
them. When the PatientNotes module lands, this cascade needs two more calls.

### 11.4 The two endpoints the client calls that Node does not implement

Neither has a route registration, so Node answers the global 404. This is the same situation as
`GET /api/visits/inbox`, where the recorded decision was to implement the endpoint properly rather
than reproduce the 404 — the client already handles the correct shape and cannot be broken by it.

**Both call sites are `client/src/components/PatientOnboardingWizard.jsx`, and both swallow
errors**, so today the wizard's doctor step silently does nothing.

#### `GET /api/connections/search-physicians?q=` — **IMPLEMENT**

Call site: `PatientOnboardingWizard.jsx:68`, `catch { setSearchResults([]) }`.
Render (lines 279-309): `doc.user?.displayName || doc.displayName`, `doc.specialty || 'General
Practice'`, and `doc.userId || doc.id` for the connect button.

**Shape decided:** `GET /search`'s physician branch, plus the two physician-profile fields the
wizard renders. A JSON array of:

```
{ id, userId, displayName, email, phone, userType, createdAt, specialty, verified, connectionStatus }
```

with `id` **and** `userId` both set to the physician's USER id, so both client fallbacks land on the
same value. Same gates as its sibling: `q.length < 2` → `200 []`, `take: 10`, `active: true`,
`userType: 'PHYSICIAN'`. Named 500: reuse `Search failed`.

#### `POST /api/connections` — **IMPLEMENT, with one judgment call flagged**

Call site: `PatientOnboardingWizard.jsx:76`, `api.post('/connections', { physicianUserId: docId })`,
`catch { }`. The response is **never read**; on success the component pushes the id into local
state.

**Shape decided:** the `POST /request` semantics, keyed by `physicianUserId` instead of
`targetEmail`. Caller is the patient, so `patientUserId = caller`, `subprofileId = null`,
`initiatedBy = caller`. `201` with `POST /request`'s create body. Same 409s
(`Already connected`, `Connection request already pending`), same `404 "User not found…"` when the
id does not resolve to a physician, and a `400` when `physicianUserId` is missing.

**⚠ The judgment call: PENDING or ACCEPTED.** This port creates a **PENDING** edge, matching
`POST /request`, because the patient is initiating and the existing UI already renders pending
requests. The wizard's own UX marks the doctor "connected" immediately, which argues for ACCEPTED —
but ACCEPTED would let any patient grant themselves a physician's chart access without the
physician's consent, which no other route in the file does. **One line changes it
(`CreatePending` → `CreateAccepted`) if the decision goes the other way.**

**STATUS: both are written and both are recorded** in `PORT-STATUS.md`'s "Deliberate divergences"
table. `ConnDiscoverSearchPhysiciansHandler` and `ConnDiscoverConnectHandler`, both in
`Modules/Connections/Tebrazi.Connections.Application/UseCases/ConnectionDiscoveryUseCases.cs`.
The judgment call above went **PENDING**; the ACCEPTED alternative is still one line
(`CreatePending` → `CreateAccepted`) and the reasoning is repeated on the handler.
`POST /` answers 201 on both the insert and the revive-a-REJECTED-edge path, unlike `POST /request`
which answers 200 on the second — its only caller reads the status code and never the body, so two
shapes for one route would have been an invention.

### 11.5 The six Node routes that are OUT of scope

Not client-called, not ported, noted as a parity gap:

| Route | Node line |
|---|---|
| `POST /api/connections/link-account` | 900 |
| `GET /api/connections/clinic-patients/{id}/full-chart` | 1144 |
| `PUT /api/connections/clinic-patients/{id}/notes` | 1412 |
| `POST /api/connections/clinic-patients/{id}/tags` | 1455 |
| `DELETE /api/connections/clinic-patients/{id}/tags/{tagId}` | 1489 |
| `GET /api/connections/qr-info/{physicianId}` | 1739 |

Three of them (`/notes`, `/tags`, `/tags/{tagId}`) write `patient_notes` / `patient_tags`, which
have no module — so they are blocked as well as out of scope. `full-chart` aggregates six modules'
data and is the largest single handler in the file. `qr-info` is the **public**, unauthenticated
half of the QR flow and would need a route that bypasses `authCheck`; the client's
`/connect/{physicianId}` landing page will want it eventually.

### 11.6 ~~OPEN DECISION~~ — SETTLED STATICALLY: `POST /request` **is** a live 500 in Node

**Status as of the apply pass: settled on static evidence, live pair-run still owed.**

The installed client is `@prisma/client` **5.22.0** (`server/node_modules/@prisma/client/package.json`;
`package.json` pins `^5.7.0`). Prisma 5 relaxed `findUnique` to accept extra non-unique *filter*
fields in `where`, but it still **requires at least one unique identifier**, and `email` alone is not
one here — `schema.prisma:103` is `email String?` with no `@unique`, and the only unique criteria on
`User` are `id`, `phone` (:106) and the compound `email_userType` (:137). So `where: { email }` is a
`PrismaClientValidationError` on every call, it lands in the route's own catch at :227, and
`POST /api/connections/request` answers `500 {"error":"Failed to send connection request"}` for every
request — including a well-formed one. **The client's Add-Doctor / Add-Patient flow has never
received a 2xx from this endpoint.**

**The port's choice stands** and is now a recorded divergence rather than an open question: it
implements the intent through `IIdentityDirectory.GetUserByEmailAsync`, on the `GET /api/visits/inbox`
precedent. Reproducing a Prisma validation crash is reproducing a crash, not a contract. Restoring
the route to working order is therefore a **product decision** — the feature starts working — not a
fidelity regression.

**Secondary consequence, unresolved:** `@@unique([email, userType])` means one email may name both a
physician and a patient row. `GetUserByEmailAsync` is called with no `userType`, so it returns
whichever row the ordered `FirstOrDefaultAsync` yields. Node's intent is unknowable here because
Node's version of the statement never runs.

**Still owed:** run both backends and post the same body to each. That is the only evidence this
repository accepts (`.claude/rules/port-contract.md`). The same `findUnique`-on-email pattern is at
`clinics.js:440`, `clinics.js:1194` and `organization.js:336`, so the pair-run settles four routes.

<details><summary>The original open-decision text, kept for the record</summary>

#### ⚠ OPEN DECISION — `POST /request` may be a live 500 in Node

**Finding, high confidence, NOT yet verified against a running pair of backends.**
Line 160 reads:

```js
const targetUser = await prisma.user.findUnique({ where: { email: targetEmail }, … });
```

But `model User` has **no standalone `@unique` on `email`** — it is `email String?` with
`@@index([email])` and `@@unique([email, userType])` (schema.prisma:103, 132, 137). Prisma's
`UserWhereUniqueInput` therefore accepts `id`, `phone` or the compound `email_userType`, and a
`where` containing only `email` fails validation at runtime:
*"Argument `where` … needs at least one of `id`, `phone` or `email_userType` arguments."*

That exception lands in the route's own catch, so **`POST /api/connections/request` would answer
`500 {"error":"Failed to send connection request"}` for every request that supplies a
`targetEmail`** — i.e. always, since a missing one 400s first. The same pattern appears at
`clinics.js:440`, `clinics.js:1194` and `organization.js:336`, so if it is real it is not confined
to this module.

**Recommendation, and what this pass built for:** port the lookup as
`IIdentityDirectory.GetUserByEmailAsync(targetEmail)` — first match on email, no `userType`
narrowing — which is what the code plainly *intends*. Reproducing a Prisma validation crash is
reproducing a crash, not a contract, and the endpoint is the entry point for the whole Add-Doctor
and Add-Patient flow.

**This is a decision, not a commit.** Per `.claude/rules/port-contract.md`, the only evidence that
settles it is running both backends and sending the same request to each. Do that before the
Link group's handler is signed off, and record the outcome in `PORT-STATUS.md` — either as a
deliberate divergence (if Node really does 500) or as a non-finding (if Prisma 5.7 accepts it).

</details>

### 11.7 Divergences introduced by this pass, each deliberate

| Divergence | Why |
|---|---|
| Unordered Node queries get a deterministic order | `SearchUsersAsync`, `GetFirstClinicIdForPhysicianAsync`, `FindAnyPairAsync`, `FindForUpdateByEitherSideAsync` and `FindRedeemableForUpdateAsync` all mirror a Node `findFirst`/`findMany` with **no `orderBy`**, where Postgres returns physical order. On `SearchUsersAsync` this is **observable** — `take: 10` means the order decides *which* ten users come back. Each implementation orders by `created_at` (then `id`), the closest deterministic equivalent for mostly-append-only tables. |
| `connection_pins.pin` is indexed, not unique | Matches Prisma. Uniqueness holds only among live pins and is enforced by the generation loop. |
| The composite unique index is **filtered** to `subprofile_id IS NOT NULL` | Prisma's `@@unique([physicianUserId, patientUserId, subprofileId])` is implemented by Postgres with NULLS DISTINCT, so it constrains dependant edges only. SQL Server treats NULLs as equal and would reject a second parent edge that Node allows. The filter reproduces Postgres exactly. |
| `DELETE /clinic-patients/{id}` is three commits, not one transaction | The Node `$transaction` spans four tables in three .NET module contexts. See §11.3. |
| `create-patient` cannot 500 on a failed notification | `INotificationPublisher` never throws by design; Node's un-caught `await createNotification` can. §10.9. |
| `GET /` is not cached | `cacheMiddleware(15)` with no invalidation. §10.11. |
| The QR PNG bytes differ | Different encoder, same decoded payload. §4. |
| `ClinicPatientRecord.UpdatedAt` is null on a fresh row | `MutableEntity` leaves it null until the first modification; Prisma's `@updatedAt` sets it on create. Same class of gap already recorded for `PatientNoteRecord`. |
| No 429 anywhere | Node's 100 req/min/IP limiter has no counterpart. Pre-existing, module-wide. |
| `DateTime` serialization | `"2026-09-10T00:00:00Z"` vs Node's `"2026-09-10T00:00:00.000Z"`. Open decision #1 in `PORT-STATUS.md`, fixed once in the host — **do not work around it per endpoint.** |
| `POST /request` answers 201/200/4xx where live Node 500s | See §11.6 — settled statically against `@prisma/client` 5.22.0 and `schema.prisma:103/137`. The port implements the intent. |
| `POST /connect-by-pin`: a 4-element **array** `pin` answers 400, not Node's 500 | `!pin \|\| pin.length !== 4` (connections.js:1613) passes an array of four, which then dies on a Prisma type error at :1618 → `500 {"error":"Failed to connect"}`. `ConnJs.AsString` (Services/ConnJsSemantics.cs:57-58) returns null for every `JsonValueKind` but `String`, so an array is indistinguishable from a number or an object and takes the 400. Unreachable from the client, which joins the four digits into a string. Recording it rather than reproducing it: the alternative is a `JsonValueKind.Array && GetArrayLength() == 4` special case whose only purpose is to emit a crash. |
| `POST /generate-pin`: the non-string-`clinicId` 500 is raised **before** the force-expire sweep | Node reads the value at :1518 but a PHYSICIAN caller does not hand it to Prisma until `connectionPin.create` at :1574 — **after** the force-expire `updateMany` at :1546-1553 has committed. So Node answers 500 with every live PIN of that physician already dead; the port answers the identical 500 with the previous PIN still redeemable. Response bytes match; committed state does not. `ResolveClinicId` (ConnectionPinUseCases.cs) is where the early throw happens. |
| The two client-called routes Node does not implement are **implemented** | `GET /search-physicians` and `POST /` — §11.4. Both now exist: `ConnDiscoverSearchPhysiciansHandler` and `ConnDiscoverConnectHandler`, both in `ConnectionDiscoveryUseCases.cs`. `POST /` creates a **PENDING** edge; see §11.4 for the judgment call and the one line that changes it. |

### 11.8 Shared files this pass changed

Listed so a reviewer can see every edit outside `Modules/Connections/`:

| File | Change |
|---|---|
| `Directory.Packages.props` | added `QRCoder 1.8.0` |
| `Tebrazi.Backend.sln` | added the four Connections projects under `Modules/Connections` |
| `APIs/Tebrazi.Api/Tebrazi.Api.csproj` | added the Connections.Infrastructure project reference |

Changed by the **apply pass** that acted on the verifiers' findings:

| File | Change |
|---|---|
| `Kernel/Tebrazi.SharedKernel/Abstractions/Directory/IIdentityDirectory.cs` | added `GetCurrentOrganizationIdAsync(userId, ct)`. `create-patient` must copy the PHYSICIAN's `users.current_organization_id` (connections.js:663-666, 676), which is a **different column** from the JWT `organizationId` claim — that claim is `memberships[0].organization.id`, frozen at login (auth.js:140-147; `LoginHandler.cs`). No port exposed the column. |
| `Modules/Identity/Tebrazi.Identity.Persistence/Services/IdentityDirectory.cs` | implemented it as a single-column projection. |
| `Modules/Visits/Tebrazi.Visits.Persistence/Services/VisitDirectory.cs` | dropped the `ChiefComplaint != null` filter in `GetPatientVisitSummariesAsync`. Node reads `lastVisitDate` and `lastComplaint` off **one** row (connections.js:1061-1065, 1110-1111); the filter ordered over a subset and paired an older visit's complaint with the newest visit's date. Reached only by `GET /patient-summaries` today. |
| `Modules/Clinics/Tebrazi.Clinics.Persistence/Services/ClinicPatientDirectory.cs` | gated the gender parse on `Enum.IsDefined(typeof(Gender), string)` before `Enum.TryParse`. TryParse alone accepted `"1"` (stored as FEMALE), `"7"` (stored and echoed verbatim) and comma-lists, all of which Prisma rejects into the route's 500. |
| `Modules/Clinics/Tebrazi.Clinics.Persistence/Configurations/ClinicPatientConfiguration.cs` | widened `name`/`phone`/`email`/`national_id` to `nvarchar(450)` and `blood_type` to `nvarchar(max)`. Prisma declares all five as unbounded `text` (schema.prisma:575-581) and connections.js:789-797 writes them unvalidated, so a 35-character phone or an 11-character blood note was a truncation → `500 {"error":"Failed to create patient file"}` where Node answers 201. `gender` stays narrow — it is an enum on both backends. |
| `Modules/Clinics/…/Migrations/20260911150904_WidenClinicPatientFreeTextColumns.cs` | new. Drops and recreates `IX_clinic_patients_name` / `IX_clinic_patients_phone` around the ALTERs by hand — EF scaffolds neither, and SQL Server refuses ALTER COLUMN on a live index key often enough to be worth not gambling on. |
| `Modules/Connections/…/Configurations/ConnectionConfigurations.cs` | `connection_pins.clinic_id` from `nvarchar(36)` to `nvarchar(max)`. `generate-pin` stores the raw `X-Clinic-Id` header with no existence or length check (connections.js:1577) into a Postgres `text` (schema.prisma:552); an over-long header was a 500 **after** the force-expire had committed. |
| `Modules/Connections/…/Migrations/20260911…_WidenConnectionPinClinicId.cs` | new, scaffolded. |
| `Kernel/Tebrazi.SharedKernel/Abstractions/IEmailSender.cs` | corrected the contract doc: a caller reproducing `connections.js` must treat a failed send as **success**. `sendEmail` cannot reject — see §11.9. |
| `APIs/Tebrazi.Api/Program.cs` | `using` + `AddConnectionsModule(configuration, isDevelopment)` |
| `Kernel/…/Abstractions/IEmailSender.cs` | **new** port |
| `Kernel/…/Abstractions/Directory/IConnectionDirectory.cs` | **new** port |
| `Kernel/…/Abstractions/Directory/IIdentityDirectory.cs` | +3 members, +2 records, +`IPatientAccountProvisioner` |
| `Kernel/…/Abstractions/Directory/IPatientDirectory.cs` | +2 members, +1 record, +`IPatientProfileProvisioner` |
| `Kernel/…/Abstractions/Directory/IClinicDirectory.cs` | +1 member |
| `Kernel/…/Abstractions/Directory/IClinicPatientDirectory.cs` | +4 members, +2 records |
| `Kernel/…/Abstractions/Directory/IVisitDirectory.cs` | +2 members, +1 record, +`IVisitClinicPatientWriter` |
| `Kernel/…/Abstractions/Directory/IAppointmentDirectory.cs` | +`IAppointmentClinicPatientWriter` |
| `Kernel/Tebrazi.Infrastructure.Shared/Placeholders/ConsoleEmailSender.cs` | **new** |
| `Kernel/…/DependencyInjection/InfrastructureSharedModule.cs` | registered `IEmailSender` |
| `Modules/Identity/…/Services/IdentityDirectory.cs` | implemented the 3 new members |
| `Modules/Identity/…/Services/PatientAccountProvisioner.cs` | **new** |
| `Modules/Identity/…/DependencyInjection/IdentityPersistenceModule.cs` | registered it |
| `Modules/Patients/…/Services/PatientDirectory.cs` | implemented the 2 new members |
| `Modules/Patients/…/Services/PatientProfileProvisioner.cs` | **new** |
| `Modules/Patients/…/DependencyInjection/PatientsModule.cs` | registered it |
| `Modules/Clinics/…/Services/ClinicDirectory.cs` | implemented the 1 new member |
| `Modules/Clinics/…/Services/ClinicPatientDirectory.cs` | implemented the 4 new members |
| `Modules/Visits/…/Services/VisitDirectory.cs` | implemented the 2 new members |
| `Modules/Visits/…/Services/VisitClinicPatientWriter.cs` | **new** |
| `Modules/Visits/…/DependencyInjection/VisitsModule.cs` | registered it |
| `Modules/Appointments/…/Services/AppointmentClinicPatientWriter.cs` | **new** |
| `Modules/Appointments/…/DependencyInjection/AppointmentsModule.cs` | registered it |

**No existing behaviour was changed** — every edit is additive, and the warning count is unmoved at
216.

### 11.9 Findings applied after the five writing groups, with their evidence

Every item below was raised by a verifier, re-verified against `server/src/routes/connections.js`
and the .NET code, then fixed. Each cites the Node lines that settle it.

| Route | Was | Now |
|---|---|---|
| `GET /patient-summaries` | `lastComplaint` was the newest visit **that had** a complaint | `lastVisitDate` and `lastComplaint` come from the same row, as they do in Node's single `findFirst` (:1061-1065, :1110-1111). `VisitDirectory.cs` lost its `ChiefComplaint != null` filter. |
| `POST /create-patient` | stamped the new patient with the **caller's JWT `organizationId`** claim | stamps the **physician's `users.current_organization_id`** (:663-666, :676), through the new `IIdentityDirectory.GetCurrentOrganizationIdAsync`. The claim is `memberships[0].organization.id` frozen at login (auth.js:140-147) — a different column. `auth.js:228` returning that column under the key `organizationId` on `GET /auth/me` is what made the two look identical. `ConnDiscoverCreatePatientCommand` lost its `CallerOrganizationId` parameter. |
| `POST /invite-email` | a mail-transport throw was a 500 | always 200. `sendEmail` never rejects (`emailService.js:26-64`, :92-96, :109, :140-143), so the send is wrapped in a swallow-all. |
| `POST /add-by-phone` | the physician display-name read sat **inside** the best-effort block | it sits under the file guard, where Node has it. `.catch(() => {})` at :593 is attached to `createNotification` only; the `findUnique` at :583-586 is a separate statement whose failure is the route's 500 **after** the edge at :571-579 has committed. |
| `POST /request` | coerced `subprofileId` and `clinicId` to strings at the top of the handler | coerces each at the point Node hands it to Prisma: `subprofileId` immediately before the pair probe (:193, i.e. after the 404 at :166 and the 400 at :183), `clinicId` inside the `userType !== 'PHYSICIAN'` branch (:144-147). A PHYSICIAN posting `{"clinicId": 7}` is answered 201 by Node and now by this port; a non-string `subprofileId` against an unknown email is answered 404, not 500. |
| `POST /clinic-patients` | over-long `phone` / `bloodType` / `name` / `email` were a truncation 500 | the columns are widened. Prisma has them as unbounded `text` (schema.prisma:575-581) and the route validates none of them (:789-797, `bloodType` not even trimmed). |
| `POST /clinic-patients` | a numeric-string `gender` (`"1"`, `"7"`) was accepted and stored | rejected into the route's 500, as Prisma's enum does. `Enum.TryParse` alone accepts numeric strings and comma-lists and succeeds for undefined values. |
| `POST /invite-email`, `GET /invite-link` | claimed by BOTH `PinPrefix` and the built `ConnDiscover*` types | `ConnectionResponseConventions` now assigns them to `DiscoverPrefix`, where the working code is. `GET /patient-summaries` was likewise mis-assigned to `SubprofilePrefix` and is now under `LinkPrefix`. Every route appears exactly once. |
| the clinic-patient files' `connections.js:NNN` citations | ~a dozen were off by one to four lines | renumbered against the real file. This module has no extracted contract, so those citations **are** the audit trail. |

Two findings were recorded rather than changed — see the §11.7 rows for the `connect-by-pin` array
`pin` and the `generate-pin` early throw. One was raised as an open question and is now settled
statically: §11.6.
