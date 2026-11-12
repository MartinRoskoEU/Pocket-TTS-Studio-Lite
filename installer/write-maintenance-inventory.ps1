param([Parameter(Mandatory)][string] $Payload, [Parameter(Mandatory)][string] $Support, [Parameter(Mandatory)][string] $Version)
$ErrorActionPreference = 'Stop'
$encoding = [System.Text.UTF8Encoding]::new($false)
$files = @(Get-ChildItem -LiteralPath $Payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($Payload.Length + 1).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
if ($files.Count -eq 0 -or $files.Count -gt 4096) { throw 'Invalid payload inventory size.' }
$manifestPath = Join-Path $Payload 'maintenance\package.json'
[IO.File]::WriteAllText($manifestPath, ([ordered]@{ SchemaVersion = 1; AppId = '336B2514-DBF3-4566-9302-95EE5D2D7DD3'; Version = $Version; Files = $files } | ConvertTo-Json -Depth 6), $encoding)
$manifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('[UninstallDelete]')
# Inno records these during Setup, so never gate them on uninstall-only state.
# Failed owned removal exits before Inno processes this list. Empty-only deletion
# preserves unknown content, including in package directories with no files.
$directories = @(Get-ChildItem -LiteralPath $Payload -Recurse -Directory | ForEach-Object {
    $_.FullName.Substring($Payload.Length + 1)
} | Sort-Object @{ Expression = { $_.Length }; Descending = $true }, @{ Expression = { $_ } })
foreach ($directory in $directories) {
    $lines.Add(('Type: dirifempty; Name: "{app}\' + $directory + '"'))
}
$lines.Add('Type: dirifempty; Name: "{app}"')
$temporaryFiles = [Collections.Generic.List[string]]::new()
$temporaryFiles.Add('[Files]')
$worker = Join-Path $Support 'worker'
$workerFiles = @(Get-ChildItem -LiteralPath $worker -Recurse -File | Sort-Object FullName)
for ($index = 0; $index -lt $workerFiles.Count; $index++) {
    $relative = $workerFiles[$index].FullName.Substring($worker.Length + 1)
    $temporaryFiles.Add(('Source: "{#SupportDir}\worker\' + $relative + '"; DestName: "maintenance-temp-' + $index + '"; Flags: dontcopy solidbreak'))
}
$temporaryFiles.Add('Source: "{#PayloadDir}\maintenance\package.json"; DestName: "maintenance-package.json"; Flags: dontcopy solidbreak')
[IO.File]::WriteAllLines((Join-Path $Support 'maintenance-files.iss'), $temporaryFiles, $encoding)
$lines.Add('[Code]')
$lines.Add('procedure MaterializeWorker;')
$lines.Add('begin')
for ($index = 0; $index -lt $workerFiles.Count; $index++) {
    $relative = $workerFiles[$index].FullName.Substring($worker.Length + 1)
    $lines.Add(('  MaterializeWorkerFile(''maintenance-temp-' + $index + ''', ''' + $relative + ''');'))
}
$lines.Add('end;')
$lines.Add('function PinWorker(Root: String): Boolean;')
$lines.Add('begin')
$lines.Add('  Result := False;')
foreach ($file in $workerFiles) {
    $relative = $file.FullName.Substring($worker.Length + 1)
    $hash = (Get-FileHash -LiteralPath $file.FullName).Hash
    $lines.Add(('  if not PinVerifiedFile(AddBackslash(Root) + ''' + $relative + ''', ''' + $hash + ''') then exit;'))
}
$lines.Add('  Result := True;')
$lines.Add('end;')
$lines.Add('function QueuePayloadFiles: Boolean;')
$lines.Add('begin')
$lines.Add('  Result := False;')
foreach ($file in $files) {
    if ($file.Path -eq 'maintenance/InstallerSupport.dll') { continue } # Inno removes its checked bootstrap last.
    $lines.Add(('  if not QueueOwnedFile(''' + $file.Path.Replace('/', '\') + ''', ''' + $file.Sha256 + ''') then exit;'))
}
$lines.Add(('  if not QueueOwnedFile(''maintenance\package.json'', ''' + $manifestHash + ''') then exit;'))
$lines.Add('  Result := True;')
$lines.Add('end;')
[IO.File]::WriteAllLines((Join-Path $Support 'maintenance-generated.iss'), $lines, $encoding)
[pscustomobject]@{ ManifestHash = $manifestHash; SupportHash = (Get-FileHash -LiteralPath (Join-Path $Support 'InstallerSupport.dll')).Hash }
