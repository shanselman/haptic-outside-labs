# Builds (Release) and starts the tray. It tries automatic device lookup, with a Settings-window wiggle as fallback.
# Click the tray icon for settings; right-click it for the quick menu.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root\src\Haptics.Tray\Haptics.Tray.csproj" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Get-ChildItem "$root\src\Haptics.Tray\bin\Release" -Recurse -Filter haptics-tray.exe | Select-Object -First 1
Start-Process $exe.FullName
