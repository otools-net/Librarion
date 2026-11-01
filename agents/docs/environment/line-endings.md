# Line endings

- `.gitattributes`: every text file is LF in the repository **and** in working copies (`* text=auto eol=lf`), except
  `.cmd`/`.bat` (CRLF); `tests/samples/**` byte for byte (`-text`).
- The system gitconfig has `core.autocrlf=true`: git prints `LF will be replaced by CRLF` warnings for files touched
  before `.gitattributes` existed. Harmless; `git -c core.safecrlf=false add/commit/status` keeps the output clean.
- `dotnet ef migrations add` writes CRLF with a BOM: normalise ([database](../development/database.md)).
- Write files with backslashes or quotes inside `$(…)` through the Write tool, not heredocs (Git Bash mangles them;
  SFE's note).
- A Python script with apostrophes inside a `python - <<'EOF'` heredoc breaks when the command holds other quoting:
  write it with the Write tool into the scratchpad and run the file.
