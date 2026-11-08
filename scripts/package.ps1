#Requires -Version 7.0
param([string]$Dotnet = 'dotnet', [string]$RimWorldDir, [string]$Bash)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$declaredVersion = ([xml](Get-Content (Join-Path $repo 'About/About.xml') -Raw)).ModMetaData.modVersion
if ($version -ne $declaredVersion) { throw 'Directory.Build.props and About.xml versions differ.' }
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function Resolve-TestBash {
    if ($Bash) { return $Bash }
    $command = Get-Command bash -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notmatch '(?i)[\\/]Windows[\\/]System32[\\/]bash\.exe$') { return $command.Source }
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $candidate = Join-Path (Split-Path $git.Source -Parent) '../bin/bash.exe'
        if (Test-Path -LiteralPath $candidate) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Bash is required for installer verification; install it or pass -Bash PATH.'
}
$out = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
function Reset-OutputDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -notin @((Join-Path $out 'mod/TurboTick'), (Join-Path $out 'headless'))) {
        throw "Unexpected output directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Output directory must not be a filesystem link: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -Path $resolved -ItemType Directory -Force | Out-Null
}
function Create-Package([string]$Directory, [string]$Path, [bool]$IncludeBase = $false) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    # Include dotfiles such as .env.example on Linux as well as Windows.
    [IO.Compression.ZipFile]::CreateFromDirectory($Directory, $Path, [IO.Compression.CompressionLevel]::Optimal, $IncludeBase)
}
function Assert-Package([string]$Path, [string[]]$AllowedAssemblies) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^|/)(Source|Networking|Simulation|Tests|Verification|artifacts|secrets|\.git|docs)(/|$)' -or
                $name -match '(?i)(PublishedFileId\.txt|\.pdb|\.rws|\.log|\.snapshot|\.cs)$') {
                throw "Local/source-only material entered the runtime package: $name"
            }
            if ($name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($name) -notin $AllowedAssemblies) {
                throw "Unexpected third-party/game assembly entered the package: $name"
            }
        }
    }
    finally { $archive.Dispose() }
}
Push-Location $repo
try {
    $buildArguments = @('build', 'Source/TurboTick.csproj', '-c', 'Release')
    if ($RimWorldDir) { $buildArguments += "-p:RimWorldDir=$RimWorldDir" }
    Run-Dotnet -Arguments $buildArguments
    Run-Dotnet -Arguments @('run', '--project', 'Tests/TurboTick.Tests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'Tests/Networking/TurboTick.Networking.Tests.csproj', '-c', 'Release')
    $testBash = Resolve-TestBash
    & (Join-Path $PSScriptRoot 'steamcmd/test-install-server.ps1') -Bash $testBash
    foreach ($script in Get-ChildItem (Join-Path $repo 'deploy/container') -File -Filter '*.sh') {
        $path = $script.FullName.Replace('\', '/')
        if ($path -match '^([a-zA-Z]):/(.*)$') { $path = '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2] }
        & $testBash -n $path
        if ($LASTEXITCODE -ne 0) { throw "Container script syntax failed: $($script.Name)" }
    }
    $mod = Join-Path $out 'mod/TurboTick'
    $headless = Join-Path $out 'headless'
    Reset-OutputDirectory $mod
    foreach ($directory in @('About', 'Languages')) {
        Get-ChildItem -LiteralPath (Join-Path $repo $directory) -File -Recurse |
            Where-Object { $_.Extension -in @('.xml', '.png', '.dds') } | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($repo, $_.FullName)
                $target = Join-Path $mod $relative
                New-Item -Path (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }
    }
    $assemblyDirectory = Join-Path $mod '1.7/Assemblies'
    New-Item $assemblyDirectory -ItemType Directory -Force | Out-Null
    $modAssemblies = @('TurboTick.dll', 'TurboTick.Networking.dll', 'TurboTick.Simulation.dll')
    foreach ($assembly in $modAssemblies) {
        Copy-Item -LiteralPath (Join-Path $repo "1.7/Assemblies/$assembly") -Destination $assemblyDirectory
    }
    foreach ($file in @('LoadFolders.xml', 'README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $mod
    }
    $modZip = Join-Path $out "TurboTick-$version-mod-1.7.zip"
    Create-Package $mod $modZip $true

    Reset-OutputDirectory $headless
    Copy-Item -LiteralPath $mod -Destination (Join-Path $headless 'TurboTick') -Recurse
    foreach ($directory in @('scripts/steamcmd', 'deploy/container')) {
        New-Item (Join-Path $headless $directory) -ItemType Directory -Force | Out-Null
    }
    foreach ($file in @('scripts/steamcmd/install-server.sh', 'scripts/steamcmd/install-server.ps1',
        'deploy/README.md', 'deploy/turbotick-headless.service', 'docker-compose.yml', '.env.example', 'README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $headless $file)
    }
    foreach ($file in Get-ChildItem (Join-Path $repo 'deploy/container') -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $headless 'deploy/container')
    }
    $headlessZip = Join-Path $out "TurboTick-$version-headless-installer.zip"
    Create-Package $headless $headlessZip
    Assert-Package $modZip $modAssemblies
    Assert-Package $headlessZip $modAssemblies
    $zip = [IO.Compression.ZipFile]::OpenRead($headlessZip)
    try {
        foreach ($required in @('.env.example', 'docker-compose.yml', 'deploy/README.md', 'scripts/steamcmd/install-server.sh')) {
            if (-not $zip.GetEntry($required)) { throw "Headless package is missing $required." }
        }
    }
    finally { $zip.Dispose() }
    # Coordinator: the 0.6.1 default server. Self-contained single file with no game assemblies, so the host
    # does not need a RimWorld installation. Published alongside the headless installer, which stays for
    # rolling back to 0.6.0.
    $coordinatorDir = Join-Path $out 'coordinator'
    if (Test-Path -LiteralPath $coordinatorDir) { Remove-Item -LiteralPath $coordinatorDir -Recurse -Force }
    Run-Dotnet @('publish', (Join-Path $repo 'Server/TurboTick.Server.csproj'), '-c', 'Release', '-r', 'linux-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $coordinatorDir)
    $coordinator = Join-Path $out "TurboTick-$version-coordinator-linux-x64"
    Move-Item -LiteralPath (Join-Path $coordinatorDir 'TurboTick.Server') -Destination $coordinator -Force
    Remove-Item -LiteralPath $coordinatorDir -Recurse -Force
    if (-not (Test-Path -LiteralPath $coordinator)) { throw 'Coordinator publish produced no binary.' }
    $hashes = Get-FileHash -LiteralPath $modZip, $headlessZip, $coordinator -Algorithm SHA256
    $lines = $hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" }
    [IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $hashes | Select-Object Hash, Path
}
finally { Pop-Location }
