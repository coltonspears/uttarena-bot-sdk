#!/usr/bin/env bash
# Packs the bot-authoring packages into the repository-local offline feed at
# ./.packages, which is the same feed layout the arena's sandboxed build uses.
# Run this once after cloning, and again whenever the SDK changes.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$repo_root/.packages"

mkdir -p "$feed"

for project in src/UttArena.Contracts/UttArena.Contracts.csproj \
               src/UttArena.GameEngine/UttArena.GameEngine.csproj \
               src/UttArena.BotSdk.CSharp/UttArena.BotSdk.CSharp.csproj; do
    echo "Packing $project"
    dotnet pack "$repo_root/$project" --configuration Release --output "$feed" --nologo
done

echo "Offline feed ready at $feed"
