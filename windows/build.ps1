# Builds FileFlipper for Windows into .\build\windows
#
#   .\windows\build.ps1                     # FileFlipper.exe (one self-contained file, no .NET install needed)
#   .\windows\build.ps1 -Package            # also FileFlipper-Windows.zip and, if Inno Setup is installed, FileFlipper-Windows-Setup.exe
#   .\windows\build.ps1 -Version 1.6.1      # set the version shown in About and in the installer
#
# Needs the .NET 8 SDK (https://dotnet.microsoft.com/download). The installer needs Inno Setup 6 (https://jrsoftware.org/isdl.php).
param(
    [string]$Version = "",
    [switch]$Package
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "build\windows"
$project = Join-Path $PSScriptRoot "FileFlipper\FileFlipper.csproj"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building FileFlipper $Version for Windows..."

New-Item -ItemType Directory -Force $out | Out-Null
# Not compressed inside the .exe: compressed single-file apps unpack into memory and use twice the RAM.
# The zip and the installer compress it anyway.
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Built $out\FileFlipper.exe"

if ($Package) {
    $zip = Join-Path $out "FileFlipper-Windows.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $out "FileFlipper.exe") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Packaged $zip"

    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if ($iscc) {
        & $iscc "/DAppVersion=$Version" "/DSourceExe=$out\FileFlipper.exe" "/DOutputDir=$out" (Join-Path $PSScriptRoot "installer\FileFlipper.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
        Write-Host "Packaged $out\FileFlipper-Windows-Setup.exe"
    } else {
        Write-Warning "Inno Setup 6 not found; skipped the installer."
    }
}
