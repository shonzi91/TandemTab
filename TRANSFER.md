# Transferring TandemTab (FinApp) to a new device / Claude account

Everything needed to pick the project up on a fresh machine and a different Claude Code account.
Nothing here contains a secret **value** — it points to where each secret lives and how to move it.

## 1. Code (git)

- **Repo:** `https://github.com/shonzi91/FinApp.git` (shown as **TandemTab** on GitHub; the local remote
  still uses the old URL, which redirects). Branch: `main`.
- ⚠️ **Push first.** As of this writing the latest work (Security Tier 1 + all of Tier 2 auth) is committed
  **locally only** — the `gcloud run deploy --source .` deploys uploaded source directly and never pushed git.
  Run `git push origin main` on the old device before you clone anywhere, or those commits are lost.
- On the new device: `git clone https://github.com/shonzi91/FinApp.git`
- The new machine/account needs **push rights** to the repo: sign in to GitHub as an owner/collaborator, or
  add the new account as a collaborator (repo → Settings → Collaborators), or transfer the repo.

## 2. Secrets & config (NOT in git)

Local `dotnet user-secrets` is empty — the real config lives as **Cloud Run environment variables**. They are
the source of truth. Current variable names (values in GCP):

| Variable | What it is |
|---|---|
| `Database__Provider` | `Postgres` in prod |
| `ConnectionStrings__FinApp` | Postgres connection string |
| `Jwt__Key` | JWT signing secret (≥32 chars) |
| `Auth__PublicBaseUrl` | Public app URL (used for OAuth + email links) |
| `Auth__Google__ClientId` / `Auth__Google__ClientSecret` | Google OAuth app |
| `BankSync__EnableBanking__ApplicationId` | Enable Banking app id (`a2415ba8-…`) |
| `BankSync__EnableBanking__PrivateKey` | Enable Banking RS256 private key (PEM contents, inline) |
| `BankSync__EnableBanking__Country` | Default country |
| _(to add)_ `Email__Host` / `Email__Username` / `Email__Password` / `Email__AppBaseUrl` | SMTP for the new email-verification feature (not set yet — verification logs the link until configured) |

**Pull the current values on the old device** (has GCP access):

```bash
gcloud run services describe finapp --region europe-west1 \
  --format="yaml(spec.template.spec.containers[0].env)"
```

Copy them into the new machine's `dotnet user-secrets` (for local runs) and/or re-supply them on the next
deploy. The **Enable Banking PEM** also exists as a standalone file on the old device:
`C:\Users\stoyan.s\Documents\0f3060b1-e197-4bfb-ac47-6039d3d22afa.pem` — copy it across if you want to run/mint
JWTs locally (in prod it's already inlined into `BankSync__EnableBanking__PrivateKey`).

## 3. Cloud / hosting access

- **GCP project:** `finapp-1111` · **Cloud Run service:** `finapp` · **region:** `europe-west1`.
- New device: install the gcloud SDK, then `gcloud auth login` with an account that has access to
  `finapp-1111` (add it in GCP IAM as **Cloud Run Admin** + **Cloud Build Editor** + **Service Account User**
  if it's a new person). Set the project: `gcloud config set project finapp-1111`.
- Deploy command used here: `gcloud run deploy finapp --source . --region europe-west1`.
- gcloud on this Windows box needed a python shim:
  `CLOUDSDK_PYTHON=…\google-cloud-sdk\platform\bundledpython\python.exe`.

## 4. Claude Code context (for the new Claude account)

- Read **[HANDOFF.md](HANDOFF.md)** first, then `README.md` and recent `git log` — that's the intended catch-up path.
- The persistent Claude **memory** lives outside the repo at
  `C:\Users\stoyan.s\.claude\projects\C--Projects-Global-Data-Api\memory\` (`MEMORY.md` + `project_finapp.md`
  point back here). It does **not** sync with a Claude account — copy that folder to the new machine's
  `~/.claude/projects/<slug>/memory/` if you want it, or rely on HANDOFF.md.
- Session transcripts don't transfer; HANDOFF.md is the durable record.

## 5. New-device sanity check

```bash
git clone https://github.com/shonzi91/FinApp.git && cd FinApp
dotnet build FinApp.sln          # expect: Build succeeded
dotnet test  FinApp.sln          # expect: all green (Domain + Server + Persistence)
```

Then wire secrets (§2), `gcloud auth login` (§3), and you're ready to run/deploy.
