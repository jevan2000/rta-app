<#
  Builds RtaSetup-<version>.exe:
    1. builds the dashboard (web/),
    2. publishes RtaApp and RtaTray self-contained (win-x64) into one folder,
    3. compiles installer/RtaSetup.iss with Inno Setup.
  Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$appOut = Join-Path $out 'app'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "Building RTA Sound Monitor $version" -ForegroundColor Cyan

$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }

Push-Location (Join-Path $root 'web')
try {
  if (-not (Test-Path node_modules)) { npm ci --no-fund --no-audit; if ($LASTEXITCODE) { throw 'npm ci failed' } }
  npm run build; if ($LASTEXITCODE) { throw 'dashboard build failed' }
} finally { Pop-Location }

foreach ($proj in 'src\Rta.App\Rta.App.csproj', 'src\Rta.Tray\Rta.Tray.csproj') {
  dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -o $appOut -p:DebugType=none -nologo -v q
  if ($LASTEXITCODE) { throw "publish failed: $proj" }
}

& $iscc "/DAppVersion=$version" "/O$out" (Join-Path $PSScriptRoot 'RtaSetup.iss')
if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }
Write-Host "Done: $(Join-Path $out "RtaSetup-$version.exe")" -ForegroundColor Green
