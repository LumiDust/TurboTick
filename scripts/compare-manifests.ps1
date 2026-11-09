#Requires -Version 7.0
<#
.SYNOPSIS
Compares two TurboTick compatibility manifests and names what actually differs.

.DESCRIPTION
The dedicated server only says that a client's fingerprint does not match. The client already writes the
full breakdown to compatibility-manifest.json, so pointing the two files at each other turns "some mod is
different" into "this file in this mod differs".

.PARAMETER HostManifest
The host's manifest, usually
  <user>\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\TurboTick\compatibility-manifest.json

.PARAMETER ClientManifest
The joining player's manifest, same path on their machine.

.EXAMPLE
./scripts/compare-manifests.ps1 -HostManifest host.json -ClientManifest friend.json
#>
param(
    [Parameter(Mandatory)][string]$HostManifest,
    [Parameter(Mandatory)][string]$ClientManifest,
    [int]$MaxPerMod = 12
)

$ErrorActionPreference = 'Stop'

function Read-Manifest([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Manifest not found: $Path" }
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $data.files) { throw "Not a TurboTick manifest (no files array): $Path" }
    $byMod = @{}
    foreach ($row in $data.files) {
        if (-not $byMod.ContainsKey($row.mod)) { $byMod[$row.mod] = @{} }
        $byMod[$row.mod][$row.file] = $row.sha256
    }
    [pscustomobject]@{
        Path       = (Resolve-Path -LiteralPath $Path).Path
        GameVersion = $data.gameVersion
        ModHash    = $data.modHash
        ByMod      = $byMod
        Order      = @($data.files | ForEach-Object { $_.mod } | Select-Object -Unique)
    }
}

$h = Read-Manifest $HostManifest
$c = Read-Manifest $ClientManifest
$differences = 0

Write-Host "host    : $($h.Path)"
Write-Host "          game $($h.GameVersion)  fingerprint $($h.ModHash)"
Write-Host "client  : $($c.Path)"
Write-Host "          game $($c.GameVersion)  fingerprint $($c.ModHash)"
Write-Host ''

if ($h.ModHash -eq $c.ModHash) {
    Write-Host 'Fingerprints are identical: the mod set and content match, so the refusal came from something else.' -ForegroundColor Yellow
    return
}

if ($h.GameVersion -ne $c.GameVersion) {
    Write-Host "GAME VERSION differs: host $($h.GameVersion), client $($c.GameVersion)" -ForegroundColor Red
    $differences++
}

# A different mod list or order changes the hash even when every file is identical, because the manifest
# appends the active load order.
if (($h.Order -join '|') -ne ($c.Order -join '|')) {
    $onlyHost = @($h.Order | Where-Object { $_ -notin $c.Order })
    $onlyClient = @($c.Order | Where-Object { $_ -notin $h.Order })
    if ($onlyHost.Count -gt 0 -or $onlyClient.Count -gt 0) {
        Write-Host 'MOD LIST differs (only one side has these):' -ForegroundColor Red
        if ($onlyHost.Count -gt 0) { Write-Host ("  host only  : " + ($onlyHost -join ', ')) }
        if ($onlyClient.Count -gt 0) { Write-Host ("  client only: " + ($onlyClient -join ', ')) }
    }
    else {
        Write-Host 'LOAD ORDER differs (same mods, different sequence):' -ForegroundColor Red
        Write-Host ("  host  : " + ($h.Order -join ' > '))
        Write-Host ("  client: " + ($c.Order -join ' > '))
    }
    $differences++
}

$mods = @($h.ByMod.Keys) + @($c.ByMod.Keys) | Select-Object -Unique | Sort-Object
foreach ($mod in $mods) {
    $hf = if ($h.ByMod.ContainsKey($mod)) { $h.ByMod[$mod] } else { @{} }
    $cf = if ($c.ByMod.ContainsKey($mod)) { $c.ByMod[$mod] } else { @{} }
    $changed = @()
    foreach ($file in ($hf.Keys + $cf.Keys | Select-Object -Unique | Sort-Object)) {
        $hv = if ($hf.ContainsKey($file)) { $hf[$file] } else { '<missing>' }
        $cv = if ($cf.ContainsKey($file)) { $cf[$file] } else { '<missing>' }
        if ($hv -ne $cv) { $changed += [pscustomobject]@{ File = $file; Host = $hv; Client = $cv } }
    }
    if ($changed.Count -eq 0) { continue }
    $differences++
    Write-Host "MOD $mod : $($changed.Count) differing file(s)" -ForegroundColor Cyan
    foreach ($row in ($changed | Select-Object -First $MaxPerMod)) {
        $where = if ($row.Host -eq '<missing>') { 'client only' } elseif ($row.Client -eq '<missing>') { 'host only' } else { 'content differs' }
        Write-Host ("  {0,-8} {1}" -f $where, $row.File)
    }
    if ($changed.Count -gt $MaxPerMod) { Write-Host "  … $($changed.Count - $MaxPerMod) more" }
}

Write-Host ''
if ($differences -eq 0) {
    Write-Host 'No per-file difference found, yet the fingerprints differ. Send both manifests for inspection.' -ForegroundColor Yellow
}
else {
    Write-Host "$differences area(s) above explain the refusal. Fix them, then reconnect." -ForegroundColor Green
}
