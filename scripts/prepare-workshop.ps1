param([string]$PackageDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PackageDirectory) { $PackageDirectory = $repo }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'workshop-local/TurboTick' }
$source = [IO.Path]::GetFullPath($PackageDirectory)
$target = [IO.Path]::GetFullPath($OutputDirectory)
$sep = [IO.Path]::DirectorySeparatorChar
if ($source -eq $target -or $source.StartsWith($target.TrimEnd($sep) + $sep, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output must be separate from the source and must not contain it.'
}
foreach ($tree in @('About', 'Languages', '1.7')) {
    $sourceTree = [IO.Path]::GetFullPath((Join-Path $source $tree))
    if ($target -eq $sourceTree -or $target.StartsWith($sourceTree + $sep, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The output must not be inside a copied source directory.'
    }
}
$assemblies = @('TurboTick.dll', 'TurboTick.Networking.dll', 'TurboTick.Simulation.dll')
$required = @('About/About.xml', 'About/Preview.png', 'LoadFolders.xml', 'LICENSE') + @($assemblies | ForEach-Object { "1.7/Assemblies/$_" })
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing $file; build the client first." }
}
$about = [xml](Get-Content -LiteralPath (Join-Path $source 'About/About.xml') -Raw)
if ($about.ModMetaData.packageId -ne 'lumidust.turbotick' -or $about.ModMetaData.name -ne 'TurboTick') {
    throw 'The source is not the canonical TurboTick mod.'
}
$existingAbout = Join-Path $target 'About/About.xml'
if (Test-Path -LiteralPath $existingAbout) {
    $existing = [xml](Get-Content -LiteralPath $existingAbout -Raw)
    if ($existing.ModMetaData.packageId -ne 'lumidust.turbotick') { throw 'The output belongs to a different mod.' }
}
$idPath = Join-Path $target 'About/PublishedFileId.txt'
$idBackup = Join-Path $repo 'workshop-local/PublishedFileId.txt'
$id = $null
if (Test-Path -LiteralPath $idPath) { $id = (Get-Content -LiteralPath $idPath -Raw).Trim() }
elseif (Test-Path -LiteralPath $idBackup) { $id = (Get-Content -LiteralPath $idBackup -Raw).Trim() }
if ($id -and $id -notmatch '^[1-9][0-9]*$') { throw 'Existing PublishedFileId.txt is invalid.' }
if ($id) {
    New-Item (Split-Path $idBackup -Parent) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($idBackup, $id)
}
function Remove-OwnedOutput([string]$Name) {
    if ($Name -notin @('1.7', 'Languages', 'docs', 'CONTRIBUTING.md', 'CHANGELOG.md')) { throw 'Unexpected cleanup entry.' }
    $destination = [IO.Path]::GetFullPath((Join-Path $target $Name))
    if (-not $destination.StartsWith($target.TrimEnd($sep) + $sep, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped the output directory.' }
    if (Test-Path -LiteralPath $destination) {
        $entry = Get-Item -LiteralPath $destination -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing cleanup through a link: $destination" }
        Write-Output "Replace or remove owned output: $destination"
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}
New-Item $target -ItemType Directory -Force | Out-Null
New-Item (Join-Path $target 'About') -ItemType Directory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $source 'About') -File | Where-Object Extension -in @('.xml', '.png', '.dds') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target 'About') -Force
}
foreach ($entry in @('1.7', 'Languages', 'docs', 'CONTRIBUTING.md', 'CHANGELOG.md')) { Remove-OwnedOutput $entry }
New-Item (Join-Path $target '1.7/Assemblies') -ItemType Directory -Force | Out-Null
foreach ($assembly in $assemblies) {
    Copy-Item -LiteralPath (Join-Path $source "1.7/Assemblies/$assembly") -Destination (Join-Path $target '1.7/Assemblies')
}
Get-ChildItem -LiteralPath (Join-Path $source 'Languages') -File -Recurse | Where-Object Extension -eq '.xml' | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath((Join-Path $source 'Languages'), $_.FullName)
    $destination = Join-Path $target "Languages/$relative"
    New-Item (Split-Path $destination -Parent) -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
foreach ($file in @('LoadFolders.xml', 'LICENSE', 'README.md')) {
    if (Test-Path -LiteralPath (Join-Path $source $file)) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination $target -Force }
}
if ($id) { [IO.File]::WriteAllText($idPath, $id) }
Write-Output "Prepared local Mod directory: $target"
Write-Output $(if ($id) { "Existing Workshop item preserved: $id" } else { 'No Workshop ID has been generated.' })
Write-Output 'No Steam upload was performed.'
