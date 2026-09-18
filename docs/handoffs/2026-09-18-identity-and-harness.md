# Handoff — 2026-09-18 evening

Written by the session that landed Identity. Read `PORT-STATUS.md` first; this file only carries
what that document does not.

## State

- `main` at `4698e6a` — Identity landed, **pushed to origin is NOT confirmed**. Local is
  **ahead 1**; the last push attempts hung inside `git credential-manager get` and were killed.
  The remote (`https://github.com/agzaid/Tebrazi_New_BackendProject.git`) sits at `6dee735`.
  **First job: push once from a context where the GCM window can appear** (VS or an interactive
  terminal), then confirm with `git ls-remote origin refs/heads/main` showing `4698e6a`.
- Build verified twice at this commit: `dotnet build Tebrazi.Backend.sln --no-incremental` →
  **0 errors, 216 warnings** — the pre-existing baseline, exactly.
- `tools/contract-diff/` is **untracked and unverified scaffolding**. Its capture/differ logic
  ran live (22 snapshots, 7 synthetic differ regressions) but it is not wired into anything and
  the Node side of the diff has never been pointed at a Node backend. Do not `git add -A` it
  without deciding its status first.

## What landed in this session

1. **DateTime wire format** (decision #1 closed): `NodeDateTimeJsonConverter` in
   `Tebrazi.Common.Api/Serialization`, registered at `Program.cs:55`. Live-verified on
   `/api/health` and byte-compared against `node -e` output.
2. **Contract harness**: `tools/contract-diff/contract-diff.mjs` + `plan.json` (59 cases). The
   53 clinical endpoints + Patients' three aggregates were exercised against `Tebrazi_Dev`
   through it; `GET /visits/specialty-template` byte-matched live Node (2085 bytes both sides,
   computed by running Node's own `specialtyTemplates.js`). Findings recorded in
   `PORT-STATUS.md`, "Contract harness".
3. **Identity**: 16 controller actions, 13 handlers. Live-verified: 404/410 shapes, forgot-
   password's always-200, OTP limiter (4th call 429), the DELETE MY ACCOUNT gate, both exports,
   profile-picture removal, sessions list. Test account on `Tebrazi_Dev`:
   `contract.test@tebrazi.test` / `ContractTest2026!` (PHYSICIAN, owns "Nile Heart Center"
   membership via `clinic_staff`, has a `physician_profiles` row) — disposable, recreated by
   re-registering after a delete-account test.
4. **Tier-1 list correction**: `change-password`, `PUT /auth/profile`, 2FA ×3 do not exist in
   Node (20 routes enumerated; no schema support either). Live Node 404s them; this port's
   `MapFallback` answers the same body. They are not portable work.

## Known hazards for the next session

- **One build at a time.** This session's two overlapping full builds corrupted outputs and
  reported 121–135 phantom errors until everything was stopped, `dotnet clean`ed and rebuilt
  serially: then 0 errors / 216 warnings. A running `dotnet run` locks DLLs — stop it first.
- `server/.env` points `DATABASE_URL` at **production Supabase**. Never start Node against it
  for a diff; the harness refuses non-localhost targets unless `--allow-remote`.
- The monorepo (one level up) has a dead `origin` (`TebRazi/tebrazi.git` → Repository not
  found) and is 4 commits ahead of nothing. Its docs were updated to 128-of-339 in commit
  `9769867`, also not pushed anywhere.
- `dashboard.doctors[]` / `stats.totalDoctors` are still unwired — `IConnectionDirectory` is
  implemented and registered; the Patients dashboard handler just does not consume it yet.
  `stats.totalDoctors` must be `Distinct().Count()` over physician ids, not row count.

## What is next, in order

1. Confirm the push (above) and re-run `git ls-remote` to prove it.
2. The dual-backend diff needs a LOCAL Postgres fixture for Node (schema from
   `prisma/schema.prisma`, seeded to mirror `Tebrazi_Dev`). Until that exists, contract work is
   one-sided.
3. Next portable module per `PROJECT_INFO.md` §3 Tier 3, largest first: **Manager, 52** — but
   only with the diff harness alive; the doc's own words call that trade.
