#!/usr/bin/env bash
# The import budgets of stage 4 (agents/tools/benchmark/README.md#what-import-measures): a generated INPX catalog and a
# Calibre library (kept in the work folder between runs) imported into a fresh database, then imported again unchanged.
#   agents/tools/benchmark/import.sh sqlite|postgres [inpx-records] [calibre-books] [work-folder]
# PostgreSQL: Docker Desktop must be running (agents/tools/postgres-dev/start.sh starts the container). Stop Docker and
# WSL afterwards on this machine (little memory). Reports: <work>/import-<source>-<provider>-<records>-<time>.json.
set -euo pipefail
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
provider="${1:?sqlite or postgres}"
inpx="${2:-1000000}"
calibre="${3:-2000000}"
work="${4:-D:/Temp/librarion-bench}"
mkdir -p "$work"
dotnet build "$repo/tests/Librarion.Benchmarks" -c Release -v q -nologo > /dev/null
bench="$repo/tests/Librarion.Benchmarks/bin/Release/net10.0/Librarion.Benchmarks.dll"
if [ "$provider" = postgres ]; then
  connection="$("$repo/agents/tools/postgres-dev/start.sh" | sed 's/Database=postgres/Database=librarion_import_bench/')"
else
  connection="Data Source=$work/import-sqlite.db"
fi
status=0
[ "$inpx" -gt 0 ] && { dotnet "$bench" import --provider "$provider" --connection "$connection" --source inpx --records "$inpx" --work "$work" || status=1; }
[ "$calibre" -gt 0 ] && { dotnet "$bench" import --provider "$provider" --connection "$connection" --source calibre --records "$calibre" --work "$work" || status=1; }
exit $status
