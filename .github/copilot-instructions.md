# Copilot instructions for this repository

Source of truth: [`docs/SPEC.md`](../docs/SPEC.md). Design rationale: [`docs/DESIGN_NOTES.md`](../docs/DESIGN_NOTES.md).

## Invariants (never violate)

- **We never move, click, steer, or focus anything.** The user drives the pointer; we observe and add haptics.
  No `SendInput`, `SetCursorPos`, `SetForegroundWindow`, `SetFocus`, hooks, injection, UIA action patterns, or
  `ValuePattern`/`TextPattern` in `src/`. Reading `SelectionItemPattern.IsSelected` is allowed; its action methods are not. `ForbiddenApiScanner` + `tests/Haptics.Windows.Tests/SafetyTests.cs` enforce this.
- Raw Input is **mouse only** (usage page 0x01, usage 0x02) with `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY`. Never keyboard,
  never `RIDEV_NOLEGACY` / `RIDEV_CAPTUREMOUSE`.
- UIA reads only control type, bounds, enabled/offscreen/password flags, PID, hashed runtime id, one parent hop, and
  slider range, plus `ClassName`, `IsSelected`, parent bounds and monitor DPI. Never `Name`, values, text, URLs. UIA runs only in the worker process (`--uia-worker`).
- An API returning `true` is `OBSERVED`, never `PHYSICAL`. Only blinded operator trials (`PhysicalEvaluator`) produce
  `PHYSICAL`. Call `ExperimentResult.Finalize()` (via `ExperimentSession.Complete()`); do not bypass it.
- Every haptic intent goes through `HapticPolicy.Evaluate`. No new source may emit around it.
- The tray is a deliberately simple sample (user decision, 2026-10-23): starts On with route R3a, auto-primes, all apps.
  It must still not reference `Haptics.Lab` or fixtures, stay passive, and keep password/exclusion silence.
- The playground is the **Showcase** (`ShowcaseForm`, `haptics-lab` with no args); experiments live on its Lab tab and
  are optional for users. Haptic meaning is fixed: Land=Hover, Land big=Click, Detent=Step, Edge=Collide;
  Align/Success/Error only from cooperating apps.
- Never invent results. Update `docs/EVIDENCE_LEDGER.md` only with real runs (`haptics-lab evidence summarize <run>`).

## Build and test

```powershell
dotnet build HapticsEverywhere.slnx
dotnet test HapticsEverywhere.slnx
src\Haptics.Lab\bin\Debug\net10.0-windows10.0.26100.0\haptics-lab.exe run-all   # automated experiments
```

Windows projects target `net10.0-windows10.0.26100.0` with `WindowsSdkPackageVersion=10.0.28000.87`
(the .NET 10 SDK does not know the 28000 TFM yet; ADR 0005).

Releases: pushing a `vX.Y.Z` tag runs `.github/workflows/release.yml`, which tests, publishes, signs (Azure Trusted
Signing) and attaches win-arm64/win-x64 zips to a GitHub release. See [`docs/RELEASING.md`](../docs/RELEASING.md).

## Adding an experiment

1. Add it to `ExperimentCatalog` (`src/Haptics.Core/Ledger.cs`) with hypotheses and dependencies.
2. Implement it under `src/Haptics.Lab/Experiments/` using `ExperimentSession`; include a negative control and a
   falsification condition; label evidence honestly.
3. Wire it in `Program.RunExperiment`, add tests, and document it in `docs/TEST_MATRIX.md`.

## Adding a semantic rule

Add the transition to `SemanticInterpreter`, its rule/intent mapping in `HapticPolicy.RuleFor`, a `VocabularyTests` case,
and (if it involves timing or ordering) a golden trace in
`tests/traces/` with an expectation in `Exp070Replay.Expectations` and a test in `ReplayTests`.
