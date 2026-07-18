# Deploy FinApp on Google Cloud Run + Neon Postgres — free & managed

The cleanest hosting for FinApp: **no VM, no Caddy, no volumes.** Cloud Run runs the container,
gives it an HTTPS URL automatically, scales to zero when idle, and builds the image for you with
Cloud Build. The database is **free managed Postgres** at [Neon](https://neon.tech).

```
Browser ──HTTPS──▶ Cloud Run (*.run.app, auto-TLS) ──▶ FinApp container (:8080) ──▶ Neon Postgres (SSL)
```

Both free tiers are generous and need no payment for a personal/family app (Cloud Run does ask for a
billing account, but the always-free allotment covers this usage).

## 1. Create a free Postgres at Neon
1. Sign up at <https://neon.tech> → **New Project** (pick an EU region, e.g. *Europe (Frankfurt)*).
2. Open **Connection Details** → choose the **`.NET`** snippet. You'll get an Npgsql string like:
   ```
   Host=ep-xxx-pooler.eu-central-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=********;SSL Mode=Require;Trust Server Certificate=true
   ```
   Copy it — that's your `ConnectionStrings__FinApp`. (Use the **pooled** host if offered.)

## 2. Set up gcloud
1. Install the gcloud CLI: <https://cloud.google.com/sdk/docs/install>.
2. ```bash
   gcloud auth login
   gcloud projects create finapp-XXXX --name FinApp     # or pick an existing project
   gcloud config set project finapp-XXXX
   gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com
   ```
   (Enabling Cloud Run requires a billing account linked to the project — the free tier still applies.)

## 3. Deploy (builds from the Dockerfile via Cloud Build)
From the repo root:
```bash
JWT=$(openssl rand -base64 48)
NEON='Host=ep-xxx-pooler.eu-central-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=********;SSL Mode=Require;Trust Server Certificate=true'

gcloud run deploy finapp \
  --source . \
  --region europe-west1 \
  --allow-unauthenticated \
  --port 8080 \
  --memory 512Mi \
  --min-instances 0 \
  --max-instances 1 \
  --timeout 3600 \
  --set-env-vars "Database__Provider=Postgres" \
  --set-env-vars "ConnectionStrings__FinApp=$NEON" \
  --set-env-vars "Jwt__Key=$JWT"
```
First deploy builds the image (a few minutes) and prints a **Service URL** like
`https://finapp-xxxxx-ew.a.run.app`. Open it → create your account.

### Why these flags
- `--max-instances 1` — FinApp's SignalR hub has **no backplane**, so it must run as a single instance
  (multiple instances would split live-sync groups). One instance is plenty for family use.
- `--min-instances 0` — scale to zero when idle = stays within the free tier (trade-off: a cold start
  on the first request after idle). Set to `1` to keep it warm (uses more quota).
- `--timeout 3600` — lets SignalR WebSocket connections stay open up to an hour; the client
  auto-reconnects after that.
- HTTPS, the domain, and the `PORT` are handled by Cloud Run; the app already listens on `:8080`.

## Updating
```bash
gcloud run deploy finapp --source . --region europe-west1   # re-uses the env vars already set
```

## Notes & hardening
- **Secrets:** the above passes `Jwt__Key` and the DB password as env vars (simplest). For better
  hygiene, store them in **Secret Manager** and use `--set-secrets` instead of `--set-env-vars`.
- **Schema:** the server runs `EnsureCreated()` on Postgres (builds tables from the model on first
  start). If the schema changes later, you'd reset the Neon DB or introduce Postgres EF migrations.
- **Backups:** Neon has its own branching/backup features in the dashboard.
- **Snapshot encryption at rest is ON in production** (since 2026-07-08). Set `Kms__KeyName=projects/…/cryptoKeys/…`
  and the server envelope-encrypts every snapshot: a fresh 256-bit data key per write (AES-256-GCM), wrapped by a
  KMS-held key that never touches the database — so a database compromise alone yields ciphertext. Legacy plaintext
  rows are migrated on startup, idempotently. **Without `Kms__KeyName` the server silently falls back to storing
  plaintext** (intended for local dev/tests), so verify it is set in any real deployment.
- **Snapshots are gzipped before they are encrypted** (envelope `ENC2:`, since 2026-07-18) — the only order that
  works, since ciphertext doesn't compress. Snapshot JSON is repetitive and the stored column shrinks ~5–7x, which
  matters because that row crosses clouds on every save (Cloud Run `europe-west1`/GCP → Neon `eu-central-1`/AWS).
  Older `ENC1:` rows are still readable and upgrade themselves on their next save; there is nothing to migrate.
  **Compare `payload=` against `stored=` in the `[save]` log line** to see the ratio a real account is getting.
- **E2E encryption is not planned** — it is incompatible with what the app already does (the server deserializes
  snapshots to build exports, and bank sync stores real transactions under a server-held key). The trust model is
  that the server may read account data; confidentiality rests on encryption at rest and access control.
