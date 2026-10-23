# ADR 0005 — C#/.NET 10 with the 28000 projection via WindowsSdkPackageVersion

**Status:** accepted

## Context
`InputHapticsManager` ships in the 10.0.28000 Windows SDK projection (Universal API Contract 19). The installed .NET
10 SDK (10.0.402) does not recognise the `net10.0-windows10.0.28000.0` TFM (NETSDK1140), and the machine only has the
26100 native Windows SDK.

## Decision
- All Windows projects target `net10.0-windows10.0.26100.0` and set `WindowsSdkPackageVersion=10.0.28000.87` in
  `Directory.Build.props`, which pulls `Microsoft.Windows.SDK.NET.Ref` 10.0.28000.87 (verified to contain the
  haptics types by reflection, see `docs/RESEARCH.md`).
- Every haptics call is guarded by `ApiInformation` and isolated in non-inlined methods so older OS builds degrade to
  `BLOCKED` instead of crashing.
- WinForms for the lab and tray (fast to iterate, no XAML toolchain); no C++/WinRT shim was needed.
- AnyCPU builds: native ARM64 on Snapdragon Surface devices, x64 in CI.

## Consequences
When the .NET SDK learns the 28000 TFM, switch to it and delete the override. If the managed projection ever proves
problematic for thread-bound behaviour, `HapticsApi` is the single seam to swap for a native shim.
