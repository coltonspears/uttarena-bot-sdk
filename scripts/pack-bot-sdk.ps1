#!/usr/bin/env pwsh
# Packs the bot-authoring packages into the repository-local offline feed at
# ./.packages, which is the same feed layout the arena's sandboxed build uses.
# Run this once after cloning, and again whenever the SDK changes.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repoRoot '.packages'

New-Item -ItemType Directory -Force -Path $feed | Out-Null

foreach ($project in @('src/UttArena.Contracts/UttArena.Contracts.csproj',
                       'src/UttArena.GameEngine/UttArena.GameEngine.csproj',
                       'src/UttArena.BotSdk.CSharp/UttArena.BotSdk.CSharp.csproj')) {
    $full = Join-Path $repoRoot $project
    Write-Host "Packing $project"
    dotnet pack $full --configuration Release --output $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw "Pack failed for $project" }
}

Write-Host "Offline feed ready at $feed"
