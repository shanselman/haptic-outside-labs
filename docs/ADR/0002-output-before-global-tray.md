# ADR 0002 — Prove output before building a global tray

**Status:** accepted

## Context
The feasibility of background haptic output is the project's critical unknown (SPEC §2.2 H1/H2). A beautiful
semantic observer is worthless as a product if the mouse cannot be actuated from the background.

## Decision
- Build EXP-010 (foreground baseline) and EXP-020 (background, routes R2/R3a/R3b) before anything that emits haptics
  in other apps.
- The tray exists as a prototype but its **On** mode is gated in code by the local evidence ledger:
  `EvidenceLedger.VerifiedBackgroundRoutes(fingerprint)` must contain a route with a `PHYSICAL` `PASS`.
- Semantic work proceeds in **dry-run** (`PolicyOutcome.DryRun`) regardless of the output gate.

## Consequences
Nobody can demo "haptics everywhere" on a machine where it has not been felt. If EXP-020 fails, the project pivots
with the semantic layer intact (SPEC §15) instead of shipping an imitation.

## Update 2026-10-23

EXP-020 physically confirmed routes R3a/R3b on the bench machine. At the user's request, the tray became a simple
sample that starts On with R3a and no longer reads the ledger. The decision's purpose (prove output before shipping
a global tray) was met; the lab gates remain.
