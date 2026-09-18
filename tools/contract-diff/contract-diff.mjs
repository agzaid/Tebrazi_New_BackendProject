#!/usr/bin/env node
// Contract diff: capture a response snapshot from one backend, then diff two snapshot trees.
//
// The port's premise is that the React client cannot tell the two backends apart, so the thing
// worth comparing is the JSON contract: status, key set, KEY ORDER, value types and the string
// FORMAT classes (an ISO timestamp with three fractional digits is a different contract from one
// with seven). Values themselves are deliberately not compared by default — the Node backend and
// the .NET backend read different databases, so equal values would only mean the fixtures matched.
// Run with --values when both backends genuinely share data.
//
// Zero dependencies. Node 18+ for global fetch.
//
//   node contract-diff.mjs capture --target dotnet --base http://localhost:5008 --out snapshots/dotnet
//   node contract-diff.mjs capture --target node   --base http://localhost:3000 --out snapshots/node
//   node contract-diff.mjs diff snapshots/node snapshots/dotnet
//
// Exit code is 1 when the diff finds anything classed `breaking`.

import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));

// ── Shape classification ─────────────────────────────────────────────────────

const ISO_MS = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;
const ISO_SUBSECOND_OTHER = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HHMM = /^\d{1,2}:\d{2}$/;
const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** The format class of a string, so "different data" does not read as "different contract". */
function stringClass(value) {
    if (value === "") return "string:empty";
    if (ISO_MS.test(value)) return "string:iso-ms";          // what JSON.stringify(new Date()) gives
    if (ISO_SUBSECOND_OTHER.test(value)) return "string:iso-other"; // 7 digits, no Z, an offset…
    if (UUID.test(value)) return "string:uuid";
    if (HHMM.test(value)) return "string:hhmm";
    if (EMAIL.test(value)) return "string:email";
    return "string";
}

/**
 * A structural fingerprint. Objects keep their key ORDER, because Express emits keys in the order
 * the handler wrote them and a reordered body is not a byte-identical body.
 */
function shapeOf(value) {
    if (value === null) return { k: "null" };
    if (Array.isArray(value)) {
        if (value.length === 0) return { k: "array", of: null, empty: true };
        // Union the elements so a heterogeneous array is not reported from its first element alone.
        const seen = [];
        for (const element of value) {
            const shape = shapeOf(element);
            if (!seen.some(s => JSON.stringify(s) === JSON.stringify(shape))) seen.push(shape);
        }
        return { k: "array", of: seen.length === 1 ? seen[0] : { k: "union", of: seen }, empty: false };
    }
    if (typeof value === "object") {
        return { k: "object", entries: Object.entries(value).map(([key, v]) => [key, shapeOf(v)]) };
    }
    if (typeof value === "string") return { k: stringClass(value) };
    if (typeof value === "number") return { k: Number.isInteger(value) ? "int" : "float" };
    if (typeof value === "boolean") return { k: "bool" };
    return { k: typeof value };
}

// ── Diff ─────────────────────────────────────────────────────────────────────

/** `breaking` = the client can observe it. `data` = the two databases simply hold different rows. */
function diffShape(expected, actual, path, findings, compareValues) {
    if (expected.k !== actual.k) {
        findings.push({
            severity: expected.k.startsWith("string") && actual.k.startsWith("string") ? "breaking" : "breaking",
            kind: expected.k.startsWith("string") && actual.k.startsWith("string") ? "string-format" : "type",
            path, expected: expected.k, actual: actual.k
        });
        return;
    }

    if (expected.k === "object") {
        const expectedKeys = expected.entries.map(([key]) => key);
        const actualKeys = actual.entries.map(([key]) => key);

        for (const key of expectedKeys)
            if (!actualKeys.includes(key))
                findings.push({ severity: "breaking", kind: "missing-key", path: `${path}.${key}` });
        for (const key of actualKeys)
            if (!expectedKeys.includes(key))
                findings.push({ severity: "breaking", kind: "extra-key", path: `${path}.${key}` });

        const common = expectedKeys.filter(key => actualKeys.includes(key));
        const orderExpected = common.join(",");
        const orderActual = actualKeys.filter(key => expectedKeys.includes(key)).join(",");
        if (orderExpected !== orderActual)
            findings.push({
                severity: "breaking", kind: "key-order", path,
                expected: orderExpected, actual: orderActual
            });

        for (const key of common) {
            const e = expected.entries.find(([k]) => k === key)[1];
            const a = actual.entries.find(([k]) => k === key)[1];
            diffShape(e, a, `${path}.${key}`, findings, compareValues);
        }
        return;
    }

    if (expected.k === "array") {
        if (expected.empty !== actual.empty) {
            findings.push({
                severity: "data", kind: "array-emptiness", path,
                expected: expected.empty ? "empty" : "populated",
                actual: actual.empty ? "empty" : "populated"
            });
            return; // nothing comparable inside an empty array
        }
        if (!expected.empty) diffShape(expected.of, actual.of, `${path}[]`, findings, compareValues);
    }
}

function diffSnapshots(expected, actual, compareValues) {
    const findings = [];

    if (expected.status !== actual.status)
        findings.push({
            severity: "breaking", kind: "status", path: "",
            expected: expected.status, actual: actual.status
        });

    if (expected.contentType !== actual.contentType)
        findings.push({
            severity: "breaking", kind: "content-type", path: "",
            expected: expected.contentType, actual: actual.contentType
        });

    if (expected.parseError || actual.parseError) {
        if (expected.parseError !== actual.parseError)
            findings.push({
                severity: "breaking", kind: "body-not-json", path: "",
                expected: expected.parseError ?? "json", actual: actual.parseError ?? "json"
            });
        return findings;
    }

    diffShape(expected.shape, actual.shape, "", findings, compareValues);

    if (compareValues) {
        const e = JSON.stringify(expected.body);
        const a = JSON.stringify(actual.body);
        if (e !== a) findings.push({ severity: "breaking", kind: "value", path: "", expected: e, actual: a });
    }

    return findings;
}

// ── Capture ──────────────────────────────────────────────────────────────────

function isLocal(base) {
    const host = new URL(base).hostname;
    return host === "localhost" || host === "127.0.0.1" || host === "::1";
}

async function login(base, credentials) {
    const response = await fetch(`${base}/api/auth/login`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ email: credentials.email, password: credentials.password })
    });
    if (!response.ok) throw new Error(`login failed on ${base}: ${response.status} ${await response.text()}`);
    const body = await response.json();
    if (!body.token) throw new Error(`login on ${base} returned no token`);
    return body.token;
}

async function capture(plan, options) {
    const tokens = {};
    for (const [role, credentials] of Object.entries(plan.roles ?? {})) {
        if (!plan.cases.some(c => c.auth === role)) continue;
        tokens[role] = await login(options.base, credentials);
    }

    mkdirSync(options.out, { recursive: true });
    let captured = 0;

    for (const testCase of plan.cases) {
        if (options.filter && !new RegExp(options.filter).test(testCase.id)) continue;

        const method = (testCase.method ?? "GET").toUpperCase();
        if (method !== "GET" && !options.allowWrites) {
            console.log(`skip  ${testCase.id} (${method}; pass --allow-writes)`);
            continue;
        }

        const headers = { accept: "application/json" };
        if (testCase.auth) headers.authorization = `Bearer ${tokens[testCase.auth]}`;
        if (testCase.clinicId) headers["x-clinic-id"] = testCase.clinicId;
        if (testCase.body !== undefined) headers["content-type"] = "application/json";

        const response = await fetch(`${options.base}${testCase.path}`, {
            method,
            headers,
            body: testCase.body === undefined ? undefined : JSON.stringify(testCase.body)
        });

        const text = await response.text();
        const snapshot = {
            id: testCase.id,
            request: { method, path: testCase.path, auth: testCase.auth ?? null },
            status: response.status,
            contentType: (response.headers.get("content-type") ?? "").split(";")[0].trim()
        };

        try {
            snapshot.body = JSON.parse(text);
            snapshot.shape = shapeOf(snapshot.body);
        } catch {
            snapshot.parseError = "not-json";
            snapshot.bodyText = text.slice(0, 400);
        }

        writeFileSync(join(options.out, `${testCase.id}.json`), JSON.stringify(snapshot, null, 2) + "\n");
        captured++;
        console.log(`ok    ${testCase.id} → ${response.status}`);
    }

    console.log(`\n${captured} snapshot(s) in ${options.out}`);
}

// ── Entry ────────────────────────────────────────────────────────────────────

function parseArgs(argv) {
    const positional = [];
    const flags = {};
    for (let i = 0; i < argv.length; i++) {
        if (argv[i].startsWith("--")) {
            const name = argv[i].slice(2);
            const next = argv[i + 1];
            if (next === undefined || next.startsWith("--")) flags[name] = true;
            else { flags[name] = next; i++; }
        } else positional.push(argv[i]);
    }
    return { positional, flags };
}

const { positional, flags } = parseArgs(process.argv.slice(2));
const command = positional[0];

if (command === "capture") {
    const base = flags.base ?? "http://localhost:5008";
    if (!isLocal(base) && !flags["allow-remote"]) {
        console.error(
            `refusing to talk to ${base}: not localhost.\n` +
            `server/.env points DATABASE_URL at a hosted Supabase instance, so a Node backend ` +
            `started with it is the PRODUCTION database. Pass --allow-remote only when you are ` +
            `certain of what is behind the URL.`);
        process.exit(2);
    }

    const planPath = resolve(flags.plan ?? join(HERE, "plan.json"));
    const plan = JSON.parse(readFileSync(planPath, "utf8"));
    const target = flags.target ?? "dotnet";

    await capture(plan, {
        base,
        out: resolve(flags.out ?? join(HERE, "snapshots", target)),
        allowWrites: Boolean(flags["allow-writes"]),
        filter: typeof flags.filter === "string" ? flags.filter : null
    });
} else if (command === "diff") {
    const expectedDir = resolve(positional[1] ?? join(HERE, "snapshots", "node"));
    const actualDir = resolve(positional[2] ?? join(HERE, "snapshots", "dotnet"));
    const compareValues = Boolean(flags.values);

    if (!existsSync(expectedDir) || !existsSync(actualDir)) {
        console.error(`missing snapshot directory: ${existsSync(expectedDir) ? actualDir : expectedDir}`);
        process.exit(2);
    }

    const names = readdirSync(expectedDir).filter(name => name.endsWith(".json"));
    let breaking = 0, data = 0, compared = 0, missing = 0;

    for (const name of names) {
        const actualPath = join(actualDir, name);
        if (!existsSync(actualPath)) {
            console.log(`MISSING  ${name} — captured from the reference backend, absent here`);
            missing++;
            continue;
        }

        const expected = JSON.parse(readFileSync(join(expectedDir, name), "utf8"));
        const actual = JSON.parse(readFileSync(actualPath, "utf8"));
        const findings = diffSnapshots(expected, actual, compareValues);
        compared++;

        if (findings.length === 0) { console.log(`match    ${expected.id}`); continue; }

        console.log(`DIFF     ${expected.id}`);
        for (const finding of findings) {
            const location = finding.path === "" ? "(response)" : finding.path;
            const detail = finding.expected === undefined
                ? ""
                : `  expected ${JSON.stringify(finding.expected)}  actual ${JSON.stringify(finding.actual)}`;
            console.log(`  [${finding.severity}] ${finding.kind} ${location}${detail}`);
            if (finding.severity === "breaking") breaking++; else data++;
        }
    }

    console.log(
        `\n${compared} compared, ${missing} missing, ` +
        `${breaking} breaking finding(s), ${data} data-only finding(s)`);
    process.exit(breaking > 0 || missing > 0 ? 1 : 0);
} else {
    console.error(
        "usage:\n" +
        "  contract-diff.mjs capture --target <name> --base <url> [--plan p.json] [--out dir]\n" +
        "                            [--filter <regex>] [--allow-writes] [--allow-remote]\n" +
        "  contract-diff.mjs diff <expected-dir> <actual-dir> [--values]");
    process.exit(2);
}
