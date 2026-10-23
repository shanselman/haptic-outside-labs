# Builds (Release) and starts the Haptics Everywhere tray sample. It starts On; wiggle the haptic mouse over the
# box it shows once to prime it. Right-click the tray icon for intensity, exclusions, status, and Off.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root\src\Haptics.Tray\Haptics.Tray.csproj" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Get-ChildItem "$root\src\Haptics.Tray\bin\Release" -Recurse -Filter haptics-tray.exe | Select-Object -First 1
Start-Process $exe.FullName
