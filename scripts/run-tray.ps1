# Builds (Release) and starts the Haptics Everywhere tray prototype. It starts Off until you consent,
# then Observe-only. "On" stays locked until EXP-020 has a physically confirmed route on this machine.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root\src\Haptics.Tray\Haptics.Tray.csproj" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Get-ChildItem "$root\src\Haptics.Tray\bin\Release" -Recurse -Filter haptics-tray.exe | Select-Object -First 1
Start-Process $exe.FullName
