---
name: tandemtab-ui
description: TandemTab's web UI design system — the shared control classes (chips, soft buttons, cards, pills, modal fields), the colour tokens, the dark-mode rule, and the scoped-CSS traps. Load this BEFORE adding or restyling any control in src/FinApp.Shared.UI, and before writing a new CSS rule in Dashboard.razor.css or app.css. Triggers: new button/toggle/switcher/segmented control, a new card or section, "the buttons don't look like the rest of the app", dark mode looks wrong, a CSS rule that "does nothing".
---

# TandemTab web UI

The app has a small set of shared control classes. **Reach for an existing one before inventing a rule.**
Most "this doesn't follow the app design" bugs are a new control that named a class and then re-specified
its look badly — or named nothing and got a browser default.

## The rule that matters most

> **Use the class. Do not restate its colours.**

Every shared class below is fully styled for **light and dark** at its base rule. A row that wraps it may
override **size** (`padding`, `font-size`, `gap`) and **layout** (`display`, `margin`). It must never
re-declare `background`, `border-color` or `color` — if you feel the need to, the base rule is wrong and
that is what to fix.

## The control vocabulary

| What you want | Class | Where it's defined |
|---|---|---|
| A toggle / segmented switcher / filter row | `.chip` on a `<button>`, `.on` for the selected one | `Dashboard.razor.css` (base `.chip`) |
| A soft pill action inside a card | `.btn-soft`, `.btn-soft.primary` for the lead action | `Dashboard.razor.css` |
| A destructive action | add `.danger` | per-component |
| A boolean setting | `<Switch>` — **never** `<input type="checkbox">` | `Components/Switch.razor` |
| A category / fund / tag icon | `<CatIcon Name="…">`, or `<Icon Name="…">` for UI glyphs | `Components/` |
| A labelled field in a modal | `<div class="modal-field"><span class="lbl-row">…</span> … </div>` | see below |
| A Pro-gated affordance | `<ProLock Feature="@PlanFeatures.X" Static="true" />` inline, `Bar="true"` for a full-width bar | `Components/ProLock.razor` |

### `.chip` — the toggle

Every `.chip` in this app is a `<button>`. There is no static-tag variant in use. The base rule carries the
pill, the border, the light/dark palette, and the filled-green `.on` state.

```razor
<span class="plan-toggle" role="group">
    <button type="button" class="chip @(v == A ? "on" : "")" @onclick="() => v = A"><Icon Name="list" /> @Loc["By date"]</button>
    <button type="button" class="chip @(v == B ? "on" : "")" @onclick="() => v = B"><Icon Name="pie" /> @Loc["By tag"]</button>
</span>
```

A switcher row needs **nothing else** to look right. Add a rule only to change size:

```css
.my-switcher .chip { font-size: .72rem; padding: 3px 9px; }   /* size only */
```

★ **This exact rule is why this skill exists.** `.chip` used to default to a peach alert-tag look, so every
toggle row restated the real look for itself — and rows that forgot shipped to production as peach pills
with invisible text in dark mode. The Trends series switcher and the trip grouping switcher both did.
The base is the toggle now; if you ever see a peach pill, the fix is the base rule, not another restatement.

### `.btn-soft` — the action inside a card

Mint-tinted pill with an icon. `.primary` fills it. Used for the trip card's actions; that is the shape any
"do something to money" button inside a card should take, not a row of underlined `.link`s.

## Colour

- Brand green `#13a06e`, deep green `#0f7a54`, tint `#eaf7f1` / `#f4fbf7`, border `#cfe9dd`.
- Dark mode swaps to `var(--tm-mint)` (`#3fe0c5`) on near-black `#131820` / `#1e2330`, borders `#2c3142`.
- Coral `var(--tm-coral)` is spending; **gold/amber reads as a warning everywhere in this app** — never use
  it for something positive. Achievement = green.
- Muted text `#8a97a0` light, `#93a3ad` dark.

**Every new rule that sets a colour needs its `html.dark` counterpart in the same block.** A rule with no
dark twin is a bug that only shows up for half the users. `tools/pairscan.js` reports partially-darkened
rules — run it before finishing:

```bash
node tools/pairscan.js
```

## Scoped-CSS traps

`Dashboard.razor.css` is Blazor **scoped** CSS: the build appends `[b-xxxxx]` to the **last** element of each
selector, and that attribute only lands on markup this component itself renders.

1. **A child component's internals are unreachable.** `.brk-item .ic svg { … }` matches nothing when the
   `<svg>` comes from `<Icon>` — the svg carries `Icon`'s scope, not the Dashboard's. Both Breakdown chevrons
   silently never rotated for this reason. **Fix:** wrap the child in a `<span>` this component owns and
   style that, or size it in `em` so the parent's `font-size` is the handle.
2. **`html.dark .foo` works** — it becomes `html.dark .foo[b-xxxxx]`. Prefer putting dark rules next to their
   light twins in the scoped file over editing `app.css`.
3. **Editing `app.css` requires a cache-bust.** Bump `?v=` in `index.html` or returning users keep the stale
   sheet. Scoped CSS needs no bump. This alone is a reason to define a new rule in the scoped file.

## Razor traps that look like CSS bugs

- **A `<label>` wrapping a custom dropdown traps its clicks** — a label re-dispatches every click inside it
  onto its first labelable descendant, pinning `CategoryPicker` open with the modal unreachable. Use
  `<div class="modal-field"><span class="lbl-row">…</span>` instead of `<label>` for any field whose control
  isn't a native input.
- **A `<button>` inside a `<button>` is not clickable.** Card-level actions must be siblings of the card's
  own head button, not nested in it.
- **A `RenderFragment` lambda containing markup must name its parameter `__builder`.** Any other name fails
  in generated code pointing at a file you did not write.
- **Animate a named class, never an element type.** The art changes and the animation dies silently.

## Before you call a UI change done

1. `node tools/pairscan.js` → 0 partially-darkened rules.
2. Open it in a running app, **in both themes**. Build-clean and test-green prove nothing about a control's
   appearance — a peach chip compiles perfectly.
3. If it renders in the Browser pane, remember the pane freezes CSS transitions at their start value; that
   looks exactly like a dead selector. Check the computed style, not the animation.
