# Domain removal from the WASM bundle — Path B (rebind the real Dashboard)

**Goal:** drop `FinApp.Domain` from the client (WASM) bundle **while keeping the thick
`Dashboard.razor` UI pixel-for-pixel identical**. Chosen over restyling the separate thin dashboard
because the user requires *exactly* the same UI, which a re-skinned parallel UI can never guarantee.

## Why this shape
Two client→domain reference paths both feed the bundle and must both be severed:
1. **`Shared.UI → Domain`** (direct `ProjectReference`) — via `BudgetingState.cs` (2162 lines, holds a
   deserialized domain `Account` and computes reads on-device), `Dashboard.razor` (6343 lines, **806**
   `State.` reads, **65** `State.Money`/**97** `Money` uses, **62** distinct modals), and `InsightNarrator.cs`.
2. **`Shared.UI → Contracts → Domain`** — `FinApp.Contracts` pulls Domain through exactly one file,
   `AccountSnapshotSerializer.cs` (a server concern; a thin client never deserializes the aggregate).

Dropping the domain is **all-or-nothing at the bundle level** — keep the `FinApp.Domain` reference until the
very last step; convert incrementally underneath it.

## Phases (each an independently-shippable, verified slice)

- **Phase 0 — Read-API coverage audit + fill gaps (IN PROGRESS).** Ensure the thin server API computes
  *everything* the thick Dashboard shows, so every `State.X` read has a DTO to bind to. Fill gaps as new
  server endpoints (additive, web-unused, unit-tested — cannot break the live app).
- **Phase 1 — Sever `Contracts → Domain`.** Move `AccountSnapshotSerializer` server-side. (Fully lands only
  after Phase 2, when `BudgetingState` stops deserializing on-device.)
- **Phase 2 — Rebind `BudgetingState` to thin DTOs, surface by surface.** Re-express its ~100 members from the
  thin DTOs instead of the domain `Account`, keeping `Dashboard.razor` markup identical. Replace the client
  `Money` type with `decimal`+currency (matching the DTOs). One verified slice per surface.
- **Phase 3 — Drop the domain.** Remove the final references, delete the on-device money model + `InsightNarrator`,
  drop the `FinApp.Domain` `ProjectReference`, confirm the WASM bundle no longer ships the domain assembly.

## Phase 0 coverage audit

**Existing thin read DTOs / GET endpoints** (cover most surfaces):
overview · runway · targets · milestones · insights · spending · wallets · savings · budgets · recurring ·
income · structure · settings · achievements · onboarding · notifications · periods · bank (status/accounts/
mappings/pending/balance-at).

**Writes** (already command endpoints from Phase-1 cutover, Sessions 44–46): expenses, deposits, savings,
structure CRUD, budgets, fund transfers + opening balances, reallocation, period lifecycle, statement import,
settlement, recurring confirm/skip, membership/invitations. Only deferred write: bank-import provenance
(`ConfirmBankMoneyOutAsTransfer`, prod-only).

**Suspected read gaps to verify/fill** (compute-heavy modal reads, mostly not obviously in a thin DTO yet):
- Goal/debt/investment modals: `ProjectInvestment`, `ProjectCashFlow`, `DebtLoanInputs`, `EffectiveSavingPace`,
  the loan simulator + investment projection figures (domain `LoanForecast`/`InvestmentForecast`).
- Health-score **trends** modal (multi-period series; `GetInsightsAsync` returns the current score/band only).
- Reallocation/savings math surfaced in UI: `MaxAdditionalSavings`, `MaxBudgetFor`, `AvailableToTransferOut*`,
  `DiscretionaryLeftovers`.
- Expense entry helpers: `SuggestExpenseCategory`, import duplicate detection (`ImportLooksDuplicate`).
- Bank review details beyond current DTOs (`BankMatchKey`, per-transaction mapping) — prod-only, bank-gated.

Next: finish mapping each of the ~150 distinct `State.` members to a DTO field or a gap ticket, then build the
gap endpoints (Phase 0) before starting the `BudgetingState` rebind (Phase 2).

## Guardrails (learned this project)
- The thin dashboard is a **plain skeleton** — do NOT swap it to `/`; the user wants the polished thick UI.
- Take a **Neon snapshot** before any deploy that changes the live front door (Path B doesn't touch storage,
  but belt-and-suspenders).
- Deploy is classifier-gated: the `run deploy` step runs from **PowerShell**, not Bash (see
  reference build/deploy notes). Post-deploy verify: run URL + tandemtab.com 200, 5 `secretKeyRef`, zero WARNING+.
