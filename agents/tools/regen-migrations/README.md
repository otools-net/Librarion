# regen-migrations

`agents/tools/regen-migrations/regen.sh` (Git Bash): removes the `Initial` migrations of both providers (`git rm`, so
they must be committed or staged first), adds them again with `dotnet ef`, normalises them to LF without BOM and stages
them. Only before 1.0, while no install exists ([database](../../docs/development/database.md)).
