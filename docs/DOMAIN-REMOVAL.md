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
  server endpoints (additive, web-unused, unit-tested — cannot break the live app). **Exception: the
  interactive "what-if" modals** (investment/loan/cash-flow sliders) re-project on every drag, so a
  per-tick server round-trip would be poor UX — instead the *pure* forecast math ships client-side (see
  the leaf-project note below) and the DTOs carry the raw projection *inputs*.
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

**Read gaps — verified against the code (Session 54):**
- **Goal/debt/investment what-if modals** (`ProjectInvestment`, `ProjectCashFlow`, `DebtLoanInputs`, the loan
  simulator, the avalanche/snowball planner): the underlying math — `LoanForecast`, `InvestmentForecast`,
  `CashFlowForecast.Project` — was verified **entirely pure** (`decimal`/`Guid`/`string` only; no `Money`,
  `Account`, or `Period`). **RESOLVED as a client-side move, not a server endpoint** — see the leaf project below.
  Still TODO: the savings DTO (`SavingBucketDto`) must carry the raw **projection inputs** the sliders re-project
  from (annual rate, term years, compounds/yr, installment, original balance, debt-as-of, planned contribution,
  current saved) — today it carries only the pre-resolved figures. And a small **cash-flow-basis read**
  (demonstrated income/spending + basis + `hasUnknown`) so the runway what-if slider can re-run `Project` locally
  (the `/runway` endpoint returns the projection but not the basis income/spending figures the slider needs).
- **Reallocation/savings caps** (`MaxAdditionalSavings`, `MaxBudgetFor(cat)`, `AvailableToTransferOut[FromFund]`):
  **genuine server gaps** — they call `Period.*After(PriorSaved)`, needing the whole account. Add to the relevant
  DTOs (savings/budgets) or a small reallocation read. `DiscretionaryLeftovers` is **already derivable** from the
  budgets DTO coverage rows — only needs `Essential` added to `BudgetRowDto` (it isn't there yet).
- **Health-score trends** modal (multi-period series): still a gap — `/insights` returns the current score/band only.
- **Expense entry helpers**: `SuggestExpenseCategory` + the "faster entry" reads (`RecentMerchants`,
  `RecentCategories`, `LastFundForCategory`, `LastExpense`) walk all periods' manual expenses — a spending-history
  read gap. `ImportLooksDuplicate` is largely covered server-side already (S52 import dedupe snapshots existing keys).
- **Bank review details** (`BankMatchKey`, per-transaction mapping) — prod-only, bank-gated; last.

### Keystone move done (Session 54): pure forecast math extracted to a domain-free leaf project
New project **`FinApp.Forecasting`** (no dependencies) now holds `LoanForecast`, `InvestmentForecast`, and the pure
`CashFlowForecast.Project` (+ `CashFlowBasis`/`CashFlowMonth`/`CashFlowProjection`). The one Period-touching helper
stayed in the domain as **`CashFlowHistory.Demonstrated`** (`FinApp.Domain.Forecasting`). `FinApp.Domain` **and**
`FinApp.Shared.UI` both reference the leaf **directly** — Shared.UI's direct ref is deliberate so the math keeps
shipping to the WASM bundle *after* `FinApp.Domain` is dropped from the client (Phase 3). Behaviour-neutral: whole
solution builds, **506 tests green**. This lets the interactive what-if modals stay client-side with zero latency,
fed by DTO input primitives, instead of round-tripping the server per slider tick.

Next: build the DTO input-field additions + the reallocation caps + cash-flow-basis read (all additive, web-unused,
unit-tested), then start the `BudgetingState` rebind (Phase 2).

## Guardrails (learned this project)
- The thin dashboard is a **plain skeleton** — do NOT swap it to `/`; the user wants the polished thick UI.
- Take a **Neon snapshot** before any deploy that changes the live front door (Path B doesn't touch storage,
  but belt-and-suspenders).
- Deploy is classifier-gated: the `run deploy` step runs from **PowerShell**, not Bash (see
  reference build/deploy notes). Post-deploy verify: run URL + tandemtab.com 200, 5 `secretKeyRef`, zero WARNING+.
