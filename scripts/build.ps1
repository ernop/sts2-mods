# Build both mods (FlatMap map + DeckView mini-cards) and (optionally) install them into the game's
# mods folder. The two are fully independent mods; this script just builds them side by side.
#
#   .\scripts\build.ps1            # build only -> flatmap\bin\flatmap.dll + deckview\bin\deckview.dll
#   .\scripts\build.ps1 -Install   # build, then copy each mod's json + dll into
#                                   # <game>\mods\flatmap\ and <game>\mods\deckview\
#   .\scripts\build.ps1 -Only flatmap    # limit to one mod (flatmap | deckview)
#
# Override the game path if it isn't the default Steam location:
#   $env:STS2 = "D:\Games\Slay the Spire 2"
#   .\scripts\build.ps1 -Public    # build the PUBLIC release variant (reverts to vanilla with a
#                                  # warning on failure, instead of the dev-default strict crash)
[CmdletBinding()]
param([switch]$Install, [switch]$Public, [ValidateSet("flatmap", "deckview")][string]$Only)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$game = if ($env:STS2) { $env:STS2 } else { "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }
$data = Join-Path $game "data_sts2_windows_x86_64"

if (-not (Test-Path (Join-Path $data "sts2.dll"))) {
    throw "Couldn't find sts2.dll under '$data'. Set `$env:STS2 to your Slay the Spire 2 install folder."
}

$mods = @(
    @{ Id = "flatmap";  Csproj = Join-Path $root "flatmap\flatmap.csproj";   Out = Join-Path $root "flatmap\bin"; Manifest = Join-Path $root "flatmap\flatmap.json" }
    @{ Id = "deckview"; Csproj = Join-Path $root "deckview\deckview.csproj";  Out = Join-Path $root "deckview\bin"; Manifest = Join-Path $root "deckview\deckview.json" }
)
if ($Only) { $mods = @($mods | Where-Object { $_.Id -eq $Only }) }

$mode = if ($Public) { "PUBLIC (revert-to-vanilla on failure)" } else { "dev/STRICT (crash on failure)" }
foreach ($mod in $mods) {
    Write-Host "Building $($mod.Id) against $data  [$mode] ..."
    dotnet build $mod.Csproj -c Release -p:Sts2Data="$data" -p:PublicBuild=$($Public.IsPresent.ToString().ToLower()) -o $mod.Out
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($mod.Id)." }

    $dll = Join-Path $mod.Out "$($mod.Id).dll"
    if (-not (Test-Path $dll)) { throw "Build reported success but $dll is missing." }
    Write-Host "Built $dll"

    if ($Install) {
        $dest = Join-Path $game "mods\$($mod.Id)"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item $dll          -Destination $dest -Force
        Copy-Item $mod.Manifest -Destination $dest -Force
        Write-Host "Installed to $dest"
    }
}
if ($Install) {
    Write-Host "Launch STS2 -> Mods menu -> enable the mod(s) -> restart."
}
