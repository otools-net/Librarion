# This machine

As found on 2026-10-31 (Windows 11).

- .NET SDK 10.0.103 (runtime 10.0.3); packages 10.0.13 run on it (assembly versions are 10.0.0.0).
- Node 24.21, npm 12.2.
- Microsoft Edge installed (the e2e Chromium channel); Playwright's WebKit, Chromium and ffmpeg in
  `%LOCALAPPDATA%\ms-playwright` (shared with SFE, same Playwright version).
- Docker Desktop 29.3, its storage (images, volumes) on D: (moved by the user 2026-11-01), not running by default: start it (`Docker Desktop.exe`, wait for `docker info`) only for a
  PostgreSQL run. Hub pulls worked on 2026-11-01 (`postgres:17` is local now; they failed through a proxy on
  10-31).
- **Little memory:** stop what you started right after use: `docker stop librarion-pg`, Docker Desktop's processes,
  `wsl --shutdown`.
- `D:\Temp` is for temporary and test data (generated catalogs, benchmark databases): ~220 GB free, C: ~26 GB.
- Shells: Git Bash (the Bash tool) and Windows PowerShell 5.1. Git's `core.autocrlf=true`: see
  [line-endings](line-endings.md).
- SFE (the design reference) is in `C:\workspace\GIT\SimpleFileExplorer`, MyHomeLib in
  `C:\workspace\GIT\reference-projects\MyHomeLib`.
