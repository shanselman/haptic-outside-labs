# Builds (Release) and opens the Haptics Lab dashboard. Pass extra args to run a CLI command instead,
# e.g.  .\scripts\run-lab.ps1 run EXP-010
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root\src\Haptics.Lab\Haptics.Lab.csproj" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Get-ChildItem "$root\src\Haptics.Lab\bin\Release" -Recurse -Filter haptics-lab.exe | Select-Object -First 1
if ($args.Count -eq 0) {
    Start-Process $exe.FullName
} else {
    & $exe.FullName @args
    exit $LASTEXITCODE
}
