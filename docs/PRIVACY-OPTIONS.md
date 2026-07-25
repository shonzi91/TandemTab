# Client architecture & privacy options — exploratory notes

**Status:** exploration only (Session 54). **No decision made, nothing built.** This captures a family of
directions discussed so they aren't lost. The current app is unaffected.

---

## The fork that decides everything: thin vs thick client

There is **one axis** underneath all of this — *who computes the money model?* — and it is (mostly) either/or:

| | **Thin client** | **Thick client** |
|---|---|---|
| Money model runs | on the **server** | on the **device** (in-browser / native) |
| Server must **read** your data | **Yes** (to compute + apply writes) | **No** (it can be dumb storage/relay) |
| Enables **native** cleanly | ✓ (native = UI over the API) | native must talk to a server that computes, or re-port the model |
| Enables **E2E / local-only / local-first** | **✗ impossible** | **✓ possible** |
| Offline | needs a caching layer (extra work) | **free** (holds + computes the whole account) |
| Cloud cost | cheaper (bounded reads, no whole-snapshot write) | whole-snapshot write is O(history) per edit |
| Risk to today's app | **high** — rebuild the working client's data layer | **none** — it's today's app |

**The key consequence:** the **privacy features below all require the thick client.** Going thin (Path B /
[DOMAIN-REMOVAL.md](DOMAIN-REMOVAL.md)) **forecloses them**. So this is a genuine fork — pick a lane:

- **Lane 1 — Thin / native-ready.** Server computes; web client thinned; native clients later. Cheaper cloud,
  smaller bundle, clean native path. **No privacy-blind-server story.** Plan: [DOMAIN-REMOVAL.md](DOMAIN-REMOVAL.md),
  [MOBILE.md](MOBILE.md). Cost: a large, risky rewrite of the working web client (no safe partial increment).
- **Lane 2 — Thick / privacy-differentiated.** Client computes; server can be made blind. Enables E2E / local-only /
  local-first as a differentiator ("your data never leaves your device" / "we can't read it"). Cost: loses the clean
  native-thin path, keeps the whole-snapshot write, and the privacy features are real projects (below).

---

## How it works **today** (the baseline — Lane-2-ish, but server *can* read)

- Thick client holds the whole account (deserialized snapshot) and computes locally; since S47 it also sends
  **mutation commands** the server applies server-side.
- **Server is the single source of truth.** Snapshots live in Postgres, **encrypted at rest** (envelope: per-write
  AES-256-GCM data key wrapped by a server-held KMS key — `ENC1:`/`ENC2:` payload prefixes). **The server can read the
  plaintext** (for exports, bank sync, and applying commands). Ratified trust model: *"the server may read your data;
  confidentiality is encryption-at-rest + access control, not server-blindness."*
- **Multi-user sync = server as referee:** a write bumps a **version token**; a stale write is detected, the server
  **reloads the winner and re-applies the change** (bounded retry → 409). Then **SignalR** pushes "account changed" to
  the other members/devices, whose clients **re-fetch** the fresh snapshot. Concurrent edits don't clobber *because the
  server reads + merges them*.

Everything below is about **removing the server's ability to read** — which is also what makes concurrent-edit
merging hard (you lose the referee).

---

## Lane 2 privacy options (all require thick; ordered by attractiveness)

### Opt-in E2E — *recommended if pursuing privacy*
Server stores **ciphertext it can't read**; the client holds a passphrase-derived key (Web Crypto: PBKDF2/Argon2 +
AES-GCM). Keeps server-backed **durability + cross-device sync** while the server goes blind — the "have your cake"
option. A new opaque payload format (e.g. `ENC-E2E:`) the server stores/relays but never unwraps fits the existing
prefix scheme.
- **Costs / disabled for E2E accounts:** revert to whole-snapshot writes (server can't apply commands to ciphertext);
  **no bank sync** (aggregator data transits the server); **exports move client-side**; and **key recovery is the #1
  problem** (forgotten passphrase = unrecoverable data → need a downloadable recovery key or explicit "we can't help").
- **Difficulty:** solo E2E **moderate** (weeks; dominated by recovery UX + feature-gating, not the crypto). **Shared**
  E2E is **hard** (per-member key wrapping, re-wrapped on membership change) — defer.

### Local-only ("on-device") mode
Data **never leaves the device** — snapshot persisted to browser **IndexedDB** (or native SQLite/SQLCipher) instead of
the server. Strongest-sounding claim, but: **solo only** (no server = no sharing/sync), **fragile on web** (browsers
evict IndexedDB; sole copy = data-loss footgun → lean on backup/restore JSON), no bank sync. **Much safer on native**
(durable encrypted device storage — this was the app's *original* local-first SQLCipher/MAUI design, since retired).
- **Difficulty:** web MVP **moderate** (~a week: IndexedDB store + local-account mode + feature-gating; + optional
  at-rest passphrase encryption). Real cost is durability messaging, not code.

### Local-first + blind sync (offline multi-user)
Offline-capable clients; a **blind** relay+store syncs between devices/members. **You already have the relay
(SignalR) + store (Postgres)** — the "broker" isn't the new part. **The hard part is conflict resolution:** syncing
*whole snapshots* between actively-editing users = last-write-wins = **silent data loss**. Doing multi-user offline
*properly* means syncing **operations** (op-log / CRDT), a **major** domain re-architecture (and the money model's
invariants make naive CRDT merges tricky). Not serverless (needs a persistent relay) — but today's SignalR is already
pinned to a single instance, so it's a marginal change.
- **Difficulty:** solo cross-device offline **tractable** (conflicts rare → version-checked LWW ok). **Shared** offline
  is the **hard quadrant** (op-sync) — a multi-month project.

---

## If pursuing Lane 2: the staging that avoids the sinkhole
1. **Solo, opt-in E2E, manual/import only** — tractable, real privacy win, no merge problem. Nail the recovery-key flow.
2. Keep **shared accounts on today's online + server-read model** (works).
3. Only tackle **shared + offline + E2E** (op-based sync) later, as a deliberate big project, if demand justifies it.

Do **not** try to land shared + offline + E2E in one move — that quadrant sinks projects.

## Honest summary
- The privacy features are genuinely on-brand and differentiating, but each is a real project, and **all of them die if
  the client goes thin.**
- **Thin (native-ready) and thick (privacy-capable) are mutually exclusive at the extreme.** The decision is: *is the
  differentiator "a great native mobile app" or "a budgeting app that literally can't read your data"?*
- Today's app sits in the middle (thick client, server-that-can-read) and **works** — there is no pressure to move
  either way until one of those two identities becomes the priority.
