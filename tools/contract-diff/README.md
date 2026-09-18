# Contract diff harness

Capture a response snapshot from one backend, then diff two snapshot trees. The port's premise
is that the React client cannot tell the two backends apart, so the thing worth comparing is
the JSON **contract**: status, key set, KEY ORDER, value types and the string FORMAT classes.
Values themselves are deliberately not compared by default — the Node backend and the .NET
backend read different databases, so equal values would only mean the fixtures matched. Run
with `--values` when both backends genuinely share data.

Zero dependencies. Node 18+.

```bash
# capture from the .NET backend (default base http://localhost:5008)
node contract-diff.mjs capture --target dotnet --base http://localhost:5008

# capture from Node (must be a LOCAL database — see the production warning below)
node contract-diff.mjs capture --target node --base http://localhost:3000

# diff: exit 1 on any `breaking` finding
node contract-diff.mjs diff snapshots/node snapshots/dotnet
```

Useful flags: `--plan <path>` (default `plan.json`), `--filter <regex>` (case id regex),
`--allow-writes` (non-GET cases are skipped without it), `--allow-remote` (see below),
`--values` (also compare raw values, key order included).

## What a snapshot records

Status, content-type, and a **shape** — every key in order, every scalar collapsed to a format
class: `string:iso-ms` (three fractional digits + `Z`, what `JSON.stringify(new Date())` emits),
`string:iso-other` (anything else — e.g. the seven-digit form the DateTime fix killed), uuid,
hh:mm, email, empty string, int/float/bool/null. Arrays union their element shapes, so a
heterogeneous array is not reported from its first element alone.

The differ reports `breaking` for status, content-type, key set/order and format-class drift,
and `data` for content differences that just mean two databases hold different rows. Its own
behaviour is pinned by a seven-case synthetic regression (see `PORT-STATUS.md`).

## ⚠ The production database

`server/.env` points `DATABASE_URL` at a hosted Supabase instance. A Node backend started with
it is the PRODUCTION database, and this harness **refuses** to capture from a non-localhost
target unless `--allow-remote` is passed. There is no legitimate diff against production: its
data is not your fixture's data. Bring Node up against a local Postgres (see
`docs/handoffs/2026-09-18-identity-and-harness.md` §next).

## Auth

`plan.json`'s `roles` block holds per-role login credentials; the runner logs in per capture
(`POST /api/auth/login` with `email`+`password`) and attaches `Authorization: Bearer` plus the
per-case `clinicId` as `X-Clinic-Id`. Cases carrying literal `00000000-…` ids are auth'd GETs
against non-existent rows — they exercise the 404 path, which is a contract.

Status as of 2026-09-18: live against .NET only (22 snapshots). The Node side of the diff has
never run — see the handoff above for what a local Node fixture needs.
