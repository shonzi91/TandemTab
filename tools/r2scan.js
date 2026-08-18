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
// Method: normalise BOTH sides to a full path with every parameter as "*", then compare for EQUALITY.
//
// ⚠️ The first cut of this script matched a route template as a REGEX anywhere in the Kotlin, and that is wrong
// in a way that silently flatters the number: the probe for `/{id}/archived` (the archived-ACCOUNTS list) was
// satisfied by `/accounts/$accountId/tags/$tagId/archived`, because a short route's pattern is a suffix of a
// longer one. Whole-path equality is the only comparison that can't do that.

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const serverDir = path.join(root, 'src', 'FinApp.Server');
const kotlinDir = path.join(root, 'android', 'app', 'src');

function walk(dir, ext, out) {
    out = out || [];
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

// --- the server's account surface ---------------------------------------------------------------------
// ⚠️ exec-in-a-loop, not String.matchAll — the node on this device predates it, and the failure is a bare
// TypeError that reads like a bug in the scan rather than a runtime gap.
const routes = [];
for (const f of walk(serverDir, '.cs')) {
    const src = fs.readFileSync(f, 'utf8');
    const re = /accounts\s*\.\s*Map(Get|Post|Put|Delete|Patch)\s*\(\s*"([^"]+)"/g;
    let m;
    while ((m = re.exec(src)) !== null) {
        // "/{id:guid}/trips/{tripId:guid}/started" → "/accounts/*/trips/*/started"
        routes.push({
            verb: m[1].toUpperCase(),
            template: m[2],
            norm: ('/accounts' + m[2]).replace(/\{[^}]+\}/g, '*'),
        });
    }
}
// Same path under two verbs is two rows — a client that can GET but not DELETE has not covered it.
const uniq = [];
const seen = {};
for (const r of routes) {
    const k = r.verb + ' ' + r.template;
    if (!seen[k]) { seen[k] = true; uniq.push(r); }
}
uniq.sort(function (a, b) { return (a.template + a.verb).localeCompare(b.template + b.verb); });

// --- every account path Kotlin builds ------------------------------------------------------------------
// String literals only. Interpolations ("$accountId", "${trip.id}") become "*", the same placeholder the
// templates use, and a query string is dropped — "/x?today=5" and "/x" are the same route.
const kotlinPaths = {};
for (const f of walk(kotlinDir, '.kt')) {
    const src = fs.readFileSync(f, 'utf8');
    const re = /"(\/accounts\/[^"]*)"/g;
    let m;
    while ((m = re.exec(src)) !== null) {
        let p = m[1].replace(/\$\{[^}]*\}/g, '*');
        // ⚠️ A `${…}` whose expression contains its OWN string literal defeats the `"[^"]*"` capture above, so
        // the path arrives truncated mid-interpolation — `…/bank/institutions${if (country == null) `. Anything
        // from an unclosed `${` onward is expression, not path; cut it. (Well-formed ones are already `*`, so
        // this cannot eat a real middle segment like `"/accounts/${account.id}/trips"`.)
        const stray = p.indexOf('${');
        if (stray !== -1) p = p.slice(0, stray);
        p = p
            .replace(/\$[A-Za-z_][A-Za-z0-9_]*/g, '*')
            .split('?')[0]
            .replace(/\/+$/, '');
        // ⚠️ A trailing interpolation glued to the last SEGMENT is a query builder, not a path segment:
        // `"…/overview${periodQ(period)}"` is `GET /overview`, and leaving the `*` on made six routes the app
        // plainly calls (overview, spending, savings, wallets, budgets, bank/institutions) read as gaps.
        // The tell is the character before it — `/` means a real parameter segment, anything else a suffix.
        if (/[^/]\*$/.test(p)) p = p.slice(0, -1);
        kotlinPaths[p] = true;
    }
}

// ⚠️ Routes a thin client must NEVER call, so counting them as gaps overstates the backlog. /snapshot is the
// THICK client's whole-aggregate channel — Android binds DTOs precisely so it doesn't carry the domain (see
// AccountSnapshotSerializer's summary, and the note under the table in docs/MOBILE.md).
const byDesign = { '/{id:guid}/snapshot': true };

const called = [], missing = [], skipped = [];
for (const r of uniq) {
    if (byDesign[r.template]) skipped.push(r);
    else if (kotlinPaths[r.norm]) called.push(r);
    else missing.push(r);
}

const inScope = called.length + missing.length;
const pct = ((called.length / inScope) * 100).toFixed(0);
console.log('R2 parity: ' + called.length + ' of ' + inScope + ' in-scope account routes called by Android (' + pct + '%)');
console.log('Uncalled:  ' + missing.length);
console.log('Excluded:  ' + skipped.length + ' (thick-client-only by design: ' + Object.keys(byDesign).join(', ') + ')');

if (process.argv.indexOf('--list') !== -1) {
    console.log('\nNOT called from Kotlin:');
    for (const r of missing) console.log('  ' + (r.verb + '      ').slice(0, 6) + ' ' + r.template);
}

// ⚠️ Still deliberately dumb: this proves a path is BUILT in Kotlin, not that the feature works. Read a
// "called" row as "not blocked", never as "done" — S103's whole finding was that two rows counted as client
// work were really missing SERVER READS, and a scan like this would have called them called.
