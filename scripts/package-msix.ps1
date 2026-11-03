#Requires -Version 7.0
# Packages the existing publish.ps1 staging folders; does not rebuild or sign their contents.
param(
    [Parameter(Mandatory)]
    [string]$Publisher,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = ([xml](Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'))).Project.PropertyGroup.Version,
    [string]$InputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish')
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'MSIX packaging requires Windows and the Windows SDK.' }
if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'Publisher must match the signing certificate subject exactly.' }
$parts = @($Version.Split('.') | ForEach-Object { [int]$_ })
if (@($parts | Where-Object { $_ -gt 65535 }).Count) { throw 'MSIX version components must be between 0 and 65535.' }
if ($parts.Count -eq 3) { $parts += 0 }
$packageVersion = $parts -join '.'
$inputPath = (Resolve-Path -LiteralPath $InputDirectory).Path
$payload = 'haptics-tray.exe', 'haptics-lab.exe', 'minimal-background-haptics.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.txt'
foreach ($arch in 'arm64', 'x64') {
    foreach ($file in $payload) {
        $path = Join-Path $inputPath "stage-win-$arch\$file"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing publish payload: $path. Run scripts\publish.ps1 -NoZip first." }
    }
}
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem -LiteralPath $sdkBin -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $makeappx) { throw 'MakeAppx.exe not found. Install the Windows 10/11 SDK packaging tools.' }

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$publisherXml = [System.Security.SecurityElement]::Escape($Publisher)
$work = Join-Path ([System.IO.Path]::GetTempPath()) "HapticsEverywhere-msix-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $packages = New-Item -ItemType Directory -Path (Join-Path $work 'packages')
    foreach ($arch in 'arm64', 'x64') {
        $stage = New-Item -ItemType Directory -Path (Join-Path $work $arch)
        Copy-Item -Path (Join-Path $inputPath "stage-win-$arch\*") -Destination $stage.FullName -Recurse
        $assets = New-Item -ItemType Directory -Path (Join-Path $stage.FullName 'Assets') -Force
        $icon = [System.Drawing.Icon]::new((Join-Path $root 'assets\app.ico'), 256, 256)
        try {
            $bitmap = $icon.ToBitmap()
            try {
                foreach ($size in 44, 50, 150) {
                    $image = [System.Drawing.Bitmap]::new($bitmap, [System.Drawing.Size]::new($size, $size))
                    try { $image.Save((Join-Path $assets.FullName "Logo$size.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
                    finally { $image.Dispose() }
                }
            }
            finally { $bitmap.Dispose() }
        }
        finally { $icon.Dispose() }
        @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="HapticsEverywhere" Publisher="$publisherXml" Version="$packageVersion" ProcessorArchitecture="$arch" />
  <Properties>
    <DisplayName>Haptics Everywhere</DisplayName>
    <PublisherDisplayName>Haptics Outside Labs</PublisherDisplayName>
    <Logo>Assets\Logo50.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.28000.0" />
  </Dependencies>
  <Resources><Resource Language="en-us" /></Resources>
  <Applications>
    <Application Id="Tray" Executable="haptics-tray.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Haptics Everywhere" Description="Haptics Everywhere tray app"
                          BackgroundColor="transparent" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" />
    </Application>
    <Application Id="Showcase" Executable="haptics-lab.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Haptics Showcase" Description="Haptics Everywhere Showcase and lab"
                          BackgroundColor="transparent" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $stage.FullName 'AppxManifest.xml') -Encoding utf8
        & $makeappx pack /o /d $stage.FullName /p (Join-Path $packages.FullName "HapticsEverywhere-$arch.msix")
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed for $arch (exit $LASTEXITCODE)." }
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $bundle = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path "HapticsEverywhere-$packageVersion.msixbundle"
    & $makeappx bundle /o /bv $packageVersion /d $packages.FullName /p $bundle
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx bundle failed (exit $LASTEXITCODE)." }
    Write-Host "Created unsigned bundle: $bundle. Sign it with a certificate whose subject is '$Publisher' before installing."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
