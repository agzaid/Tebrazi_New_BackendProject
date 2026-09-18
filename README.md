# Tebrazi Backend (.NET)

A .NET 10 / SQL Server port of `server/` (Node + Express + Prisma + PostgreSQL), built on the
KACCC modular-monolith architecture.

**The contract is fixed: this backend returns the same JSON as the Node one.** The React client
switches over by changing `VITE_API_URL` and nothing else. When porting an endpoint, read the
Node handler first and match its status codes and body shape exactly — including its quirks.

---

## 1 · Solution map

```
server-dotnet/
├── Kernel/
│   ├── Tebrazi.SharedKernel            CQRS interfaces, base entities, exceptions,
│   │                                   permissions, ICurrentUser / IClinicContext
│   └── Tebrazi.Infrastructure.Shared   the mediator, BaseDbContext, EF stores, logging
├── APIs/
│   ├── Tebrazi.Common.Api              BaseApiController, error middleware, auth filters
│   └── Tebrazi.Api                     the host: Program.cs + controllers
├── Modules/
│   ├── Identity/                       users, orgs, subscriptions, auth
│   ├── Clinics/                        clinics, staff, PINs, invitations     ← complete
│   ├── Patients/                       profile, family, health records
│   └── Documents/                      file storage port
├── docs/PORT-STATUS.md                 what is done, what is not, and why
├── docs/ENDPOINT-INVENTORY.md          all 386 Node endpoints
└── docs/client-endpoints.txt           the 339 the React client actually calls
```

Each module has up to four projects, and the dependency direction never reverses:

| Project | Holds | May reference |
|---|---|---|
| `*.Domain` | entities, enums, domain rules | SharedKernel only |
| `*.Application` | use-case handlers, DTOs, port interfaces | its Domain + SharedKernel |
| `*.Persistence` | DbContext, EF configurations, stores | its Application + Infrastructure.Shared |
| `*.Infrastructure` | external adapters (hashing, tokens, disk, HTTP) | its Persistence |

---

## 2 · Request flow

```
Controller  →  IMediator.Send(Command|Query)  →  Handler  →  Store  →  DbContext
   thin            resolves the one handler      business     EF        SaveChanges
```

Around it, in `Program.cs` order: `ExceptionHandlingMiddleware` → Serilog request logging →
response compression → CORS → static uploads → authentication → authorization → controllers.

---

## 3 · Conventions

**Controllers are thin.** Build a Command or Query, `Send` it, return the result. No business
logic, and never a `DbContext`.

**One handler per request type.** `IRequest<TResult>` + `IRequestHandler<TRequest, TResult>` from
`Tebrazi.SharedKernel.Mediation`.

> **Do not add MediatR.** This solution has its own mediator. If the package were referenced,
> `using MediatR;` would compile and silently resolve a *different* `IMediator` — a mistake that
> reads as correct. (This bit the KACCC codebase.)

**Stores stage; handlers commit.** A write store calls `Add`/`Update`/`Remove` and never
`SaveChanges`. The handler owns the unit of work, which is what lets one handler write several
stores in one transaction.

**Transactions go through `ExecuteInTransactionAsync`.** Retry-on-failure is enabled, and a
manually opened transaction throws *"the configured execution strategy does not support
user-initiated transactions"*. The delegate may run more than once after a transient fault, so
keep non-database side effects (email, SMS) outside it.

**Enums are strings, in the model and on the wire.** The React client compares against
`"PHYSICIAN"`, `"ACCEPTED"`, and so on. Ordinals would also make every raw SQL report wrong the
moment a member is inserted mid-enum.

**Never inject another module's `DbContext`.** Cross-module needs go through a published port —
`IClinicAccessEvaluator`, `IFileStorageService`. In KACCC, five of six modules injected the
concrete `IdentityDbContext`, and that is what made the module boundaries fictional.

**Every `AddDbContext` calls `ApplyGlobalSettings`.** A bare `UseSqlServer` silently loses
retry-on-failure and the cartesian-explosion guard, and nothing in the build will tell you.

**Page in SQL.** `EfReadStore.PageAsync` issues one `COUNT` and one `Skip/Take`. Do not
materialise a table and slice it in memory.

---

## 4 · Authentication and roles

Three independent layers — an endpoint may use any combination:

| Layer | Applied with | Source of truth |
|---|---|---|
| Authenticated | `[Authorize]` | HS256 JWT |
| User type | `[RequireUserType("PHYSICIAN")]` | `userType` claim |
| Clinic permission | `[RequireClinicPermission(ClinicPermission.ViewPatientFiles)]` | `ClinicStaff` row + `ClinicPermissionMatrix` |

**JWT claims are the Node payload's own key names** — `id`, `email`, `role`, `userType`,
`displayName`, `organizationId` — not the WS-Federation URIs. With a matching `Jwt:Secret`,
tokens issued by either backend are accepted by the other, which is what makes a gradual cutover
possible. *(Verified in both directions.)*

**Passwords are bcrypt cost 10**, matching Node's `bcrypt.hash(password, 10)`. Changing the
algorithm would lock out every existing user; raising the work factor is only possible as a
rehash-on-successful-login migration.

**Clinic permissions** are a direct port of `server/src/services/clinicPermissions.js`: four roles
× fourteen boolean keys, per-staff overrides merged over the role preset, unknown roles falling
back to `RECEPTIONIST`. Physicians bypass the matrix entirely. Keep
`ClinicPermissionMatrix.Presets` value-for-value identical to the JS table — a divergence splits
the two backends' authorization silently.

---

## 5 · Adding an endpoint

1. **Read the Node handler.** Note its status codes and exact body shape.
2. `*.Application/UseCases/{Commands|Queries}/<Name>/` — the request record and its handler.
3. `*.Application/ApiModels/Responses/` — the response record, named to serialize to the Node
   field names. Add `[JsonPropertyName]` where camelCase alone will not produce them.
4. Store method in `*.Persistence/Stores/` if new data access is needed.
5. Controller action: build, `Send`, return.
6. Mark the row `DONE` in `docs/ENDPOINT-INVENTORY.md`.
7. Verify against the Node response — same status, same keys, same values.

## 6 · Adding a module

1. Four projects under `Modules/<Name>/`, referenced per the table in §1.
2. `<Name>DbContext : BaseDbContext, I<Name>DbContext`, registered once via `ApplyGlobalSettings`
   with its own `__EFMigrationsHistory_<Name>` table.
3. `Add<Name>Module(configuration)` in Infrastructure, wiring persistence + application.
4. One line in `Program.cs`.
5. `dotnet ef migrations add Initial<Name> --project Modules/<Name>/Tebrazi.<Name>.Persistence --startup-project APIs/Tebrazi.Api --context <Name>DbContext`

---

## 7 · Running it

```bash
dotnet ef database update --project Modules/Identity/Tebrazi.Identity.Persistence --startup-project APIs/Tebrazi.Api --context IdentityDbContext
```

```bash
dotnet run --project APIs/Tebrazi.Api
```

Point the React client at it in `client/.env`:

```bash
VITE_API_URL=http://localhost:5185/api
```

### Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings:TebraziDb` | SQL Server connection |
| `Jwt:Secret` | **Must equal the Node `JWT_SECRET`** for interchangeable tokens |
| `Jwt:Lifetime` | Token lifetime, default `7.00:00:00` |
| `Cors:Origins` | Comma-separated allowed origins |
| `FileStorage:RootPath` | Upload directory, served at `/uploads` **and** `/api/files` |

**Never commit a real secret.** `appsettings.Development.json` holds a placeholder. Supply the
real one out of band:

```bash
dotnet user-secrets set "Jwt:Secret" "<the same value as the Node JWT_SECRET>" --project APIs/Tebrazi.Api
```

---

## 8 · What is NOT ported yet

`docs/PORT-STATUS.md` is authoritative and is updated as each module lands. As of 2026-09-02:
**44 of the 339 endpoints the client calls**, across Auth (4), Clinics (19), Patients (21), plus
`/api/health` and the `/api` banner.

Infrastructure still to build: Redis caching, rate limiting, the reminder scheduler background
service, email/SMS/Twilio integrations, PDF generation, and the AI gateway.

**Deliberate omissions, carried over from the Node behaviour rather than fixed here:**

- Any physician gets physician-level clinic access even to a clinic they do not own
  (`ClinicAccessEvaluator`, step 2). That is what the Node middleware does. Worth tightening —
  in both backends at once, so their authorization does not diverge.
- Staff invitation by email resolves the invitee by email ALONE, so where one address holds both
  a physician and a patient account it picks an arbitrary one.

- Login does not write a `Session` row. Neither does the Node route — only the phone
  claim-account flow does — so the active-sessions screen shows only devices claimed that way.
- The clinic context does not fall back to `body.clinicId`. Reading the body to make an
  authorization decision means buffering it before model binding; use a route or query parameter.

---

## 9 · Swagger / OpenAPI

Swagger UI is at **`/swagger`**; the raw document is at `/openapi/v1.json`.

On by default in Development only. Set `Swagger:Enabled` to `true` (or `false`) to override —
it is off elsewhere on purpose, because the document maps the whole API surface including which
endpoints are unauthenticated.

**To call a protected endpoint:** POST `/api/auth/login`, copy `token` from the response, click
**Authorize**, paste the token (no `Bearer` prefix). It persists across reloads.

Document generation uses the built-in `Microsoft.AspNetCore.OpenApi`. Swashbuckle is present
only for its UI middleware.

- `TebraziDocumentTransformer` — title, description, JWT bearer scheme.
- `TebraziOperationTransformer` — marks which operations need a token, adds the `X-Clinic-Id`
  header to clinic-scoped ones, documents the Node-shaped 401/403 bodies, and writes the
  `[RequireUserType]` / `[RequireClinicPermission]` guards into each operation's description.

**`AddOpenApi` is called from the host, not from `Common.Api`.** The XML-comment source
generator hooks that call site and can only see XML docs visible from the calling assembly, so
calling it inside the shared library leaves every controller summary blank.

Add `[ProducesResponseType<T>(StatusCodes.Status200OK)]` to new actions — without it the document
claims 200 for an endpoint that returns 201.
