# Build deterministic, user-installable archives for both mods (FlatMap map + DeckView mini-cards).
[CmdletBinding()]
param(
    # CI-only: package prebuilt/stub DLLs (flatmap\bin\flatmap.dll + deckview\bin\deckview.dll) without
    # rebuilding (no game install needed).
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

if (-not $SkipBuild) {
    # Ship the PUBLIC variant: on an incompatible/failed hook it reverts to vanilla with a warning
    # instead of the dev-default strict crash. Functionally identical to the dev build you tested;
    # only the failure mode differs. (Do your in-game checks on a normal build.ps1 build first.)
    Write-Host "Building PUBLIC release DLLs ..."
    & (Join-Path $PSScriptRoot "build.ps1") -Public
    if ($LASTEXITCODE -ne 0) { throw "Public build failed." }
}

$mods = @(
    @{ Id = "flatmap";  Dll = Join-Path $root "flatmap\bin\flatmap.dll";   Manifest = Join-Path $root "flatmap\flatmap.json" }
    @{ Id = "deckview"; Dll = Join-Path $root "deckview\bin\deckview.dll";  Manifest = Join-Path $root "deckview\deckview.json" }
)

Add-Type -AssemblyName System.IO.Compression
New-Item -ItemType Directory -Force -Path $dist | Out-Null

foreach ($mod in $mods) {
    $id = $mod.Id
    if (-not (Test-Path $mod.Dll)) {
        throw "Missing '$($mod.Dll)'$(if (-not $SkipBuild) { " after the public build" })."
    }

    $manifestText = [IO.File]::ReadAllText($mod.Manifest)
    $workshopPath = Join-Path $root "workshop\content\$id.json"
    $workshopText = [IO.File]::ReadAllText($workshopPath)
    if ($manifestText -ne $workshopText) {
        throw "Root and Workshop manifests differ for '$id'. Synchronize them before packaging."
    }

    $manifest = $manifestText | ConvertFrom-Json
    $baseName = "$id-$($manifest.version)-sts2-$($manifest.min_game_version)"
    $zipPath = Join-Path $dist "$baseName.zip"
    $hashPath = "$zipPath.sha256"
    Remove-Item $zipPath, $hashPath -Force -ErrorAction SilentlyContinue

    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($source in @(
                @{ Path = $mod.Dll; Name = "$id/$id.dll" },
                @{ Path = $mod.Manifest; Name = "$id/$id.json" }
            )) {
                $entry = $archive.CreateEntry(
                    $source.Name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $entryStream = $entry.Open()
                try {
                    $bytes = [IO.File]::ReadAllBytes($source.Path)
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -Encoding ascii -NoNewline $hashPath
    Write-Host "Created $zipPath"
    Write-Host "Created $hashPath"
}
