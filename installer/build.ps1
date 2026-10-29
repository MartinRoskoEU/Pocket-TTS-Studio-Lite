[CmdletBinding()]
param(
    [string] $IsccPath,
    [string] $PocketTtsRuntimeSourceDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'PocketTTSStudioLite\PocketTTSStudioLite.csproj'
$artifacts = Join-Path $repo 'artifacts\installer'

function Invoke-Checked([string] $Program, [string[]] $Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else { $IsccPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe' }
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
$compilerVersion = & $IsccPath --version
if ($LASTEXITCODE -ne 0 -or ($compilerVersion -join '') -notmatch '\b([7-9]|[1-9][0-9]+)\.[0-9]+\.[0-9]+') { throw 'Inno Setup 7 or later is required.' }

# Evaluate the same project properties that publish uses. No second release constant.
$properties = @('-nologo', '-getProperty:Version,AssemblyVersion,PocketTtsRuntimeSourceDir,TargetPlatformMinVersion', '-p:Configuration=Release', '-p:PublishProfile=installer-x64')
if ($PocketTtsRuntimeSourceDir) { $properties += "-p:PocketTtsRuntimeSourceDir=$PocketTtsRuntimeSourceDir" }
$evaluated = & dotnet msbuild $project @properties
if ($LASTEXITCODE -ne 0) { throw 'MSBuild property evaluation failed.' }
$settings = ($evaluated -join "`n" | ConvertFrom-Json).Properties
$version = $settings.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $settings.AssemblyVersion -ne "$version.0") {
    throw 'The Windows release must have a three-part numeric Version and matching four-part assembly version.'
}
$runtime = Join-Path $settings.PocketTtsRuntimeSourceDir 'pocket-tts.exe'
$runtimeLicense = Join-Path $settings.PocketTtsRuntimeSourceDir 'LICENSE'
# The production source already owns this pinned identity. Read it instead of keeping another hash constant.
$cliSource = Get-Content (Join-Path $repo 'PocketTTSStudioLite\Services\PocketTtsModelSetupService.cs') -Raw
$hashMatch = [regex]::Match($cliSource, 'ValidatedRuntimeSha256\s*=\s*"([A-Fa-f0-9]{64})"')
if (-not $hashMatch.Success) { throw 'Cannot resolve the production runtime identity.' }
$expectedHash = $hashMatch.Groups[1].Value
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or -not (Test-Path -LiteralPath $runtimeLicense -PathType Leaf)) {
    throw 'Packaging requires the official pinned Pocket TTS executable and adjacent LICENSE. No runtime or model download is performed.'
}
if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Unverified Pocket TTS runtime payload.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Visual Studio C++ x64 build tools are required for the installer CPU check.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$toolchain = Get-ChildItem (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$dumpbin = Join-Path $toolchain.FullName 'bin\Hostx64\x64\dumpbin.exe'

# Fresh staging prevents a stale DLL, model, or prior output from entering the installer.
# Never recursively delete user-supplied paths or application data.
$staging = Join-Path $artifacts ('staging\' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'
$support = Join-Path $staging 'support'
$output = Join-Path $repo 'release'
New-Item -ItemType Directory -Force $payload, $support, $output | Out-Null
$publishArguments = @('publish', $project, '--configuration', 'Release', '-p:PublishProfile=installer-x64', "-p:PublishDir=$payload\")
if ($PocketTtsRuntimeSourceDir) { $publishArguments += "-p:PocketTtsRuntimeSourceDir=$PocketTtsRuntimeSourceDir" }
Invoke-Checked dotnet $publishArguments
foreach ($required in @('PocketTTSStudioLite.exe', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'PocketTTSStudioLite.pri', 'runtime\pocket-tts\LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Incomplete self-contained payload: $required" }
}
if ((Get-FileHash (Join-Path $payload 'runtime\pocket-tts\pocket-tts.exe')).Hash -ne $expectedHash) { throw 'Published runtime identity mismatch.' }
$fileVersion = (Get-Item (Join-Path $payload 'PocketTTSStudioLite.exe')).VersionInfo
if ($fileVersion.ProductVersion -ne $version -or $fileVersion.FileVersion -ne "$version.0") { throw 'Published version does not match MSBuild.' }
$runtimeConfig = Get-Content (Join-Path $payload 'PocketTTSStudioLite.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or
    $runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'framework') { throw 'The .NET publish is framework-dependent.' }
if (Get-ChildItem $payload -Recurse -File | Where-Object { $_.Extension -in '.safetensors', '.onnx', '.pt', '.pth', '.wav', '.mp3' }) { throw 'Model/audio data must not be distributed in the installer.' }

$compile = '@call "' + $vcvars + '" >nul' + "`r`n" +
    '@cl.exe /nologo /LD /MT /O2 /W4 /WX /guard:cf /Fo"' + $support + '\InstallerSupport.obj" "' +
    (Join-Path $PSScriptRoot 'InstallerSupport.cpp') + '" /link /OUT:"' + $support + '\InstallerSupport.dll" /IMPLIB:"' +
    $support + '\InstallerSupport.lib" /DYNAMICBASE /NXCOMPAT' + "`r`n@exit /b %errorlevel%`r`n"
$compileScript = Join-Path $support 'compile.cmd'
Set-Content -LiteralPath $compileScript -Value $compile -Encoding ASCII
Invoke-Checked $env:ComSpec @('/d', '/c', $compileScript)

# Audit direct AND delayed native imports. The current payload uses the Windows
# inbox UCRT, not a shared VC++ redistributable. Fail if that dependency changes.
$imports = foreach ($file in (Get-ChildItem $payload, $support -Recurse -File | Where-Object Extension -in '.dll', '.exe')) {
    $lines = & $dumpbin /nologo /dependents $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect native imports: $($file.Name)" }
    foreach ($line in $lines) {
        if ($line -match '^\s+([\w.-]+\.dll)\s*$') {
            $name = $matches[1]
            if ($name -match '^(vcruntime|msvcp|concrt|vcomp)\d') { throw "New VC++ redistributable dependency requires provisioning: $name" }
            if ($name -notmatch '^(api-ms|ext-ms)-' -and -not (Test-Path (Join-Path $file.DirectoryName $name)) -and
                -not (Test-Path (Join-Path $payload $name)) -and -not (Test-Path (Join-Path $env:SystemRoot "System32\$name"))) {
                throw "Unresolved native dependency: $($file.Name) -> $name"
            }
            [pscustomobject]@{ File = $file.FullName.Substring($staging.Length + 1); Import = $name }
        }
    }
}
$imports | Export-Csv (Join-Path $staging 'native-imports.csv') -NoTypeInformation

Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD_PARTY_NOTICES.md') $payload
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$assets = Get-Content (Join-Path $repo 'PocketTTSStudioLite\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
# Retain original package license/notice files, including nested SDK framework notices.
foreach ($package in $assets.libraries.PSObject.Properties) {
    $packagePath = Join-Path $packageRoot $package.Name.ToLowerInvariant()
    $noticeFiles = Get-ChildItem $packagePath -Recurse -File | Where-Object { $_.Name -match '^(license|licence|notice|third.party.notices|copying|sdk_license)(\.|$|-)' }
    foreach ($notice in $noticeFiles) {
        $relative = $notice.FullName.Substring($packagePath.Length).TrimStart('\')
        $destination = Join-Path (Join-Path $licenses $package.Name.Replace('/', '\')) $relative
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $destination
    }
}
$netVersion = $runtimeConfig.runtimeOptions.includedFrameworks[0].version
$netLicenses = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$netVersion"
New-Item -ItemType Directory -Force (Join-Path $licenses 'dotnet') | Out-Null
Copy-Item (Join-Path $netLicenses 'LICENSE.TXT'), (Join-Path $netLicenses 'THIRD-PARTY-NOTICES.TXT') (Join-Path $licenses 'dotnet')
Copy-Item (Join-Path (Split-Path $IsccPath -Parent) 'license.txt') (Join-Path $licenses 'Inno-Setup-license.txt')
$rustLicenses = Join-Path $PSScriptRoot 'licenses\pocket-tts'
foreach ($component in (Get-Content (Join-Path $rustLicenses 'components.json') -Raw | ConvertFrom-Json)) {
    if ($component.license -eq 'MPL-2.0') {
        $sourceArchive = Join-Path $rustLicenses $component.source_archive
        if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash -ne $component.checksum) {
            throw "Missing/modified MPL source archive: $($component.name)"
        }
    }
}
foreach ($notice in @('Cargo.lock', 'THIRD-PARTY-NOTICES.txt', 'SUPPLEMENTAL-NOTICES.txt', 'SOURCE-AVAILABILITY.txt')) {
    if (-not (Test-Path (Join-Path $rustLicenses $notice) -PathType Leaf)) { throw "Missing Rust notice: $notice" }
}
Copy-Item (Join-Path $PSScriptRoot 'licenses\*') $licenses -Recurse
$licenseCount = (Get-ChildItem $licenses -Recurse -File).Count
if ($licenseCount -lt 10) { throw 'Dependency license collection is incomplete.' }

Invoke-Checked $IsccPath @('--quiet-progress', "--define=AppVersion=$version", "--define=PayloadDir=$payload", "--define=SupportDir=$support", "--define=OutputDir=$output", (Join-Path $PSScriptRoot 'PocketTTSStudioLite.iss'))
$installer = Join-Path $output "PocketTTSStudioLite-$version-win-x64-Setup.exe"
Get-Item $installer | Select-Object FullName, Length
Get-FileHash $installer -Algorithm SHA256
Write-Host "Installer built from $staging. No installation/uninstallation was performed."
