#Requires -Version 7.0
# Container/manifest smoke test only: inert payloads are not runnable apps or hardware evidence.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path ([System.IO.Path]::GetTempPath()) "HapticsEverywhere-msix-test-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $files = 'haptics-tray.exe', 'haptics-lab.exe', 'minimal-background-haptics.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.txt'
    foreach ($arch in 'arm64', 'x64') {
        $stage = New-Item -ItemType Directory -Path (Join-Path $work "stage-win-$arch")
        foreach ($file in $files) { Set-Content -LiteralPath (Join-Path $stage.FullName $file) -Value "inert fixture: $arch $file" }
    }
    $publisher = 'CN=Haptics & Everywhere'
    & "$PSScriptRoot\package-msix.ps1" -Publisher $publisher -Version 1.2.3 -InputDirectory $work -OutputDirectory $work
    $bundle = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $work 'HapticsEverywhere-1.2.3.0.msixbundle'))
    try {
        $reader = [System.IO.StreamReader]::new($bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $packages = @($manifest.Bundle.Packages.Package)
        if ($packages.Count -ne 2) { throw 'Expected two architecture packages.' }
        foreach ($arch in 'arm64', 'x64') {
            $package = @($packages | Where-Object Architecture -eq $arch)
            if ($package.Count -ne 1 -or $package[0].Version -ne '1.2.3.0') { throw "Incorrect package/version for $arch." }
            $entry = $bundle.GetEntry($package[0].FileName)
            $path = Join-Path $work "$arch.msix"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path)
            $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
            try {
                $reader = [System.IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
                try { [xml]$app = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($app.Package.Identity.Publisher -ne $publisher -or $app.Package.Identity.ProcessorArchitecture -ne $arch) {
                    throw "Publisher escaping or architecture identity failed for $arch."
                }
                $apps = @($app.Package.Applications.Application.Executable)
                if ($apps.Count -ne 2 -or 'haptics-tray.exe' -notin $apps -or 'haptics-lab.exe' -notin $apps) {
                    throw 'Tray and Showcase must both be registered.'
                }
                foreach ($file in $files) {
                    $reader = [System.IO.StreamReader]::new($zip.GetEntry($file).Open())
                    try { $actual = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($actual -cne (Get-Content -LiteralPath (Join-Path $work "stage-win-$arch\$file") -Raw)) {
                        throw "Payload changed: $arch $file."
                    }
                }
                foreach ($size in 44, 50, 150) {
                    if (-not $zip.GetEntry("Assets/Logo$size.png")) { throw "Missing $size logo." }
                }
            }
            finally { $zip.Dispose() }
        }
    }
    finally { $bundle.Dispose() }

    foreach ($version in '1.2.65536', '1.2.3-preview') {
        $rejected = $false
        try { & "$PSScriptRoot\package-msix.ps1" -Publisher $publisher -Version $version -InputDirectory $work -OutputDirectory $work }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid version was accepted: $version." }
    }
    Remove-Item -LiteralPath (Join-Path $work 'stage-win-x64\LICENSE')
    $rejected = $false
    try { & "$PSScriptRoot\package-msix.ps1" -Publisher $publisher -Version 1.2.3 -InputDirectory $work -OutputDirectory $work }
    catch {
        if ($_.Exception.Message -notlike '*Missing publish payload:*LICENSE*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Missing license was accepted.' }
    Write-Host 'MSIX smoke tests passed (container/manifest checks only; install and hardware behavior not tested).'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
