# This machine

As found on 2026-10-31 (Windows 11).

- .NET SDK 10.0.103 (runtime 10.0.3); packages 10.0.13 run on it (assembly versions are 10.0.0.0).
- Node 24.21, npm 12.2.
- Microsoft Edge installed (the e2e Chromium channel); Playwright's WebKit, Chromium and ffmpeg in
  `%LOCALAPPDATA%\ms-playwright` (shared with SFE, same Playwright version).
- Docker Desktop 29.3 with a proxy that refuses Docker Hub: `docker pull` fails; no PostgreSQL image locally. The
  PostgreSQL test run is CI's for now ([testing-backend](../development/testing-backend.md)).
- Shells: Git Bash (the Bash tool) and Windows PowerShell 5.1. Git's `core.autocrlf=true`: see
  [line-endings](line-endings.md).
- SFE (the design reference) is in `C:\workspace\GIT\SimpleFileExplorer`, MyHomeLib in
  `C:\workspace\GIT\reference-projects\MyHomeLib`.
