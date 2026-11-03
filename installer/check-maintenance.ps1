[CmdletBinding()]
param([string] $IsccPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo ('artifacts\installer-checks\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
function Checked([string] $Program, [string[]] $Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio x64 C++ build tools are required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$compile = '@call "' + $vcvars + '" >nul' + "`r`n" +
    '@cl.exe /nologo /std:c++20 /EHsc /LD /MT /O2 /W4 /WX /guard:cf /Fo"' + $stage + '\InstallerSupport.obj" "' +
    (Join-Path $PSScriptRoot 'InstallerSupport.cpp') + '" /link /OUT:"' + $stage + '\InstallerSupport.dll" /IMPLIB:"' +
    $stage + '\InstallerSupport.lib" /DYNAMICBASE /NXCOMPAT' + "`r`n@exit /b %errorlevel%`r`n"
$compilePath = Join-Path $stage 'compile.cmd'
Set-Content -LiteralPath $compilePath -Value $compile -Encoding ASCII
Checked $env:ComSpec @('/d', '/c', $compilePath)
Checked dotnet @('run', '--project', (Join-Path $repo 'tests\PocketTTSStudioLite.MaintenanceChecks'), '-c', 'Release', '--', (Join-Path $stage 'InstallerSupport.dll'))
Checked dotnet @('build', (Join-Path $PSScriptRoot 'Maintenance\PocketTTSStudioLite.Maintenance.csproj'), '-c', 'Release')

# Compile script/events against inert fixture files. Disable all setup EXE output.
$payload = Join-Path $stage 'payload'
$worker = Join-Path $stage 'worker'
New-Item -ItemType Directory -Force (Join-Path $payload 'maintenance'), $worker | Out-Null
New-Item -ItemType Directory -Force (Join-Path $payload 'empty\nested') | Out-Null
Set-Content -LiteralPath (Join-Path $payload 'fixture.txt') -Value 'Inert compiler fixture; not a product payload.'
Set-Content -LiteralPath (Join-Path $worker 'PocketTTSStudioLite.Maintenance.exe') -Value 'Never executed.'
Copy-Item -LiteralPath (Join-Path $stage 'InstallerSupport.dll') -Destination (Join-Path $payload 'maintenance\InstallerSupport.dll')
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$inventory = & (Join-Path $PSScriptRoot 'write-maintenance-inventory.ps1') -Payload $payload -Support $stage -Version $version
$generated = Get-Content -LiteralPath (Join-Path $stage 'maintenance-generated.iss')
$removalEntries = @($generated | Where-Object { $_ -like 'Type: dirifempty;*' })
$expectedEntries = @('Type: dirifempty; Name: "{app}\empty\nested"', 'Type: dirifempty; Name: "{app}\maintenance"',
    'Type: dirifempty; Name: "{app}\empty"', 'Type: dirifempty; Name: "{app}"')
if (($removalEntries -join "`n") -cne ($expectedEntries -join "`n")) { throw 'Uninstall directory registration must include empty payload directories, deepest first, without uninstall-time Check conditions.' }
if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else { $IsccPath = Join-Path $repo 'artifacts\installer\tools\inno-7.1.0\ISCC.exe' }
}
Checked $IsccPath @('--quiet-progress', '--output=no', "--define=AppVersion=$version", "--define=PayloadDir=$payload", "--define=SupportDir=$stage",
    "--define=OutputDir=$stage", "--define=PackageManifestHash=$($inventory.ManifestHash)", "--define=SupportHash=$($inventory.SupportHash)", (Join-Path $PSScriptRoot 'PocketTTSStudioLite.iss'))
Write-Host "Maintenance fixtures and Inno syntax passed. Evidence: $stage. No setup output, installer execution, installed-state or registry changes."
