# ADR 0001 — One lab, many isolated probes

**Status:** accepted

## Context
The spec warns against both throwaway sample projects and one giant EXE where hardware failures, COM hangs, test
automation, and a tray UI share a fault domain (SPEC §0, §3.3).

## Decision
One solution, one evidence vocabulary (`Haptics.Core`), one ledger, one executable for the lab (`haptics-lab`), but
**process isolation at run time**:

- The dashboard launches each experiment as a child process (`haptics-lab run EXP-…`) and kills automated ones that
  exceed 3 minutes.
- UI Automation always runs in a separate worker process (`--uia-worker`) on a windowless MTA thread.
- The fixture is its own process (`haptics-lab fixture`) that publishes ground truth.
- The tray (`haptics-tray`) is a separate executable that references only `Haptics.Core` and `Haptics.Windows`.

## Consequences
Hangs in UIA providers or experiments cannot freeze the dashboard or the input path. Raw Input's
one-registration-per-process rule is naturally respected. The cost is a little process plumbing (JSON lines over
stdio), which is tested (EXP-043).
