# Backlog (fg-backlog)

**Command:** `/fg-backlog`
**Phase:** Forward (before `/reversa-requirements`)

---

## 📋 The product manager with the spreadsheet

The product manager doesn't write code or decide the architecture. They write down what customers ask for, rate each request for value and effort, keep the queue in order and, when the time comes, hand the top of the queue to the team. Their spreadsheet never loses a row: whatever was discarded stays on record, with the reason.

---

## What it does

`fg-backlog` records the features you describe in the chat and keeps them in a prioritized backlog tied to Reversa's forward cycle. It only records and suggests: it doesn't run the pipeline, doesn't create feature folders and doesn't change code.

Each item gets an ID (`BL-001`, `BL-002`...) that is never reused, not even when the item is discarded. Priority comes from **Value × Effort**. You can pin an item to a position by hand, and the rest of the queue stays sorted automatically.

Once an item is linked to a folder in `_reversa_forward/`, its status is no longer manual and reflects the folder's actual stage: `requirements`, `plan`, `coding` (with `N de M` actions) or `done`.

This skill belongs to the `@flaviogarrido/reversa` fork and doesn't exist in the original `reversa` package.

---

## Operations

| Operation | Example request | What happens |
|-----------|-----------------|--------------|
| Add | `add: export orders as CSV, high value, low effort` | Assigns the next ID with status `proposta` and shows the position and quadrant. If Value or Effort is missing, it asks before saving. |
| Edit | `change BL-002 effort to high` | Updates the item and recalculates the automatic order. |
| Change status | `mark BL-004 as ready` | Only for items without a folder: `proposta`, `pronta` or `descartada`. Discarding requires a reason. |
| Reorder | `BL-007 to position 2` | Pins the item (`manual` order). `back to automatic` undoes it. |
| Link to folder | `link BL-001 to folder 012-login-sso` | Links the item to the feature folder, with your confirmation, and derives its status from it. |
| Link specs | `BL-003 affects sdd/pedidos.md` | Records which legacy specs or components the item affects. |
| Prepare execution | `take the top 2 to the pipeline` | Suggests `/reversa-requirements` (one item) or `/reversa-forward-autonomous` (several) and hands over the description ready to paste. It doesn't run the command. |

---

## How it prioritizes

| Level | Value | Effort |
|-------|-------|--------|
| high | 3 | 3 |
| medium | 2 | 2 |
| low | 1 | 1 |

The score is Value ÷ Effort. The automatic order uses the score (descending), then Value, then the oldest ID. The quadrant (quick win, big bet, filler or avoid) is informative only.

`done` items move to the bottom of the table, and discarded items leave the active table and go to the **Descartados** section.

---

## Status tied to the forward cycle

| What the feature folder contains | Status |
|----------------------------------|--------|
| No `requirements.md` | `vazio` (with a warning) |
| `requirements.md`, no `roadmap.md` | `requirements` |
| `roadmap.md`, no `actions.md` | `plan` |
| `actions.md` with pending actions | `coding (N de M)` |
| `actions.md` with every action done | `done` |

A `done` item with no addendum in `_reversa_sdd/addenda/` gets a hint to run `/reversa-sync`. A folder that disappeared triggers a `pasta ausente` warning, and the item keeps its last status.

---

## What it produces

| File | Content |
|------|---------|
| `_reversa_sdd/backlog.md` | Ordered table of active items, descriptions, discarded section and warnings |
| `_reversa_sdd/backlog-log.md` | Change history with date, author, field, old and new value, and reason. Append-only. |

Both files are written atomically. They are the only files the skill writes.

---

## When to use

Use it whenever a feature idea comes up and you want to record it before opening the forward cycle, or when you want to decide what goes into the next `/reversa-forward-autonomous`:

```
/fg-backlog
```

Once the feature folders are created, run `/fg-backlog` again to link each item to its folder.
