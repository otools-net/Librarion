#!/usr/bin/env bash
# Regenerates the Initial migrations of both providers (before 1.0 only: agents/docs/development/database.md).
# Run from anywhere; the migration files must be committed or staged (they are removed with git rm).
set -euo pipefail
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$repo"
files=$(git ls-files src/Librarion.Core/Data/Migrations)
[ -n "$files" ] && git rm -qf $files
dotnet tool restore > /dev/null
dotnet ef migrations add Initial --context SqliteLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Sqlite
dotnet ef migrations add Initial --context PostgresLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Postgres
for f in src/Librarion.Core/Data/Migrations/Sqlite/*.cs src/Librarion.Core/Data/Migrations/Postgres/*.cs; do
  sed -i '1s/^\xEF\xBB\xBF//; s/\r$//' "$f"
done
git add src/Librarion.Core/Data/Migrations
