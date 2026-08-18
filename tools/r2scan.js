#!/usr/bin/env node
// R2 parity: which server ACCOUNT routes does the Android client actually call?
//
// ★ Why this exists: S103 reported the gap as "61 of 99", counted by eye. Run as a script the same day it was
// 76 of 118. A hand count of a hundred routes is wrong every time, and worse, it is wrong in a way that looks
// authoritative in a handoff. This is the instrument — re-run it rather than re-counting.
//
//   node tools/r2scan.js          → the summary
//   node tools/r2scan.js --list   → and every uncalled route
//
// Method: take every `accounts.Map{Verb}("<template>")` in FinApp.Server, turn its template into a regex with
// the route params wildcarded, and look for a matching literal URL anywhere in the Kotlin client. Deliberately
// dumb: it proves a path is *mentioned*, not that the feature works. Treat a "called" row as "not blocked".

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const serverDir = path.join(root, 'src', 'FinApp.Server');
const kotlinDir = path.join(root, 'android', 'app', 'src');

function walk(dir, ext, out = []) {
    if (!fs.existsSync(dir)) return out;
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) {
            if (e.name === 'build' || e.name === 'obj' || e.name === 'bin') continue;
            walk(p, ext, out);
        } else if (e.name.endsWith(ext)) out.push(p);
    }
    return out;
}

// --- the server's account surface -------------------------------------------------------------------
// ⚠️ exec-in-a-loop, not String.matchAll — the node on this device is old enough to lack it, and the failure is
// a bare TypeError that reads like a bug in the scan rather than a runtime gap.
const routes = [];
for (const f of walk(serverDir, '.cs')) {
    const src = fs.readFileSync(f, 'utf8');
    const routeRe = /accounts\s*\.\s*Map(Get|Post|Put|Delete|Patch)\s*\(\s*"([^"]+)"/g;
    let m;
    while ((m = routeRe.exec(src)) !== null) routes.push({ verb: m[1].toUpperCase(), template: m[2] });
}
// Same path under two verbs is two rows — a client that can GET but not DELETE has not covered it.
const uniq = [...new Map(routes.map(r => [`${r.verb} ${r.template}`, r])).values()]
    .sort((a, b) => (a.template + a.verb).localeCompare(b.template + b.verb));

// --- what Kotlin mentions ----------------------------------------------------------------------------
const kotlin = walk(kotlinDir, '.kt').map(f => fs.readFileSync(f, 'utf8')).join('\n');

// "/{id:guid}/trips/{tripId:guid}/started" → /[^"'\s]+/trips/[^"'\s]+/started
function toProbe(template) {
    return new RegExp(
        template
            .replace(/[.+*?^$()[\]\\]/g, '\\$&')
            .replace(/\{[^}]+\}/g, '[^"\'`\\s/]+')
        + '(?![A-Za-z0-9-])');
}

// ⚠️ Routes a thin client must NEVER call, so counting them as gaps overstates the backlog. /snapshot is the
// THICK client's whole-aggregate channel — Android binds DTOs precisely so it doesn't carry the domain (see
// AccountSnapshotSerializer's summary, and the note under the table in docs/MOBILE.md).
const byDesign = ['/{id:guid}/snapshot'];

const called = [], missing = [], skipped = [];
for (const r of uniq) {
    if (byDesign.indexOf(r.template) !== -1) skipped.push(r);
    else (toProbe(r.template).test(kotlin) ? called : missing).push(r);
}

const inScope = called.length + missing.length;
const pct = ((called.length / inScope) * 100).toFixed(0);
console.log(`R2 parity: ${called.length} of ${inScope} in-scope account routes called by Android (${pct}%)`);
console.log(`Uncalled:  ${missing.length}`);
console.log(`Excluded:  ${skipped.length} (thick-client-only by design: ${byDesign.join(', ')})`);

if (process.argv.includes('--list')) {
    console.log('\nNOT called from Kotlin:');
    for (const r of missing) console.log(`  ${r.verb.padEnd(6)} ${r.template}`);
}
