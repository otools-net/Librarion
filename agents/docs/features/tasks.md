# Library roots, scans, task runs and schedules

Code: `src/Librarion.Core/Import/LibraryRoots.cs` (`LibraryOptions`, root checks), `Core/Tasks/` (`BackgroundTasks`,
`TaskSchedules`, `TaskScheduleWorker`, `ScheduleCalculator` ported from SFE), `Core/Import/LibraryScanner` (the work:
[archives](archives.md)), entities `Data/Entities/Tasks/TaskRun.cs`, `LibraryRoot` in `Catalog/Book.cs`;
`Api/Endpoints/TaskEndpoints.cs`. Tests: `TaskApiTests`, `LibraryScannerTests`. Ported from SFE's
`background-tasks.md` and `schedules.md`. How runs are followed on screen: [task-progress](task-progress.md).

## Roots

- A library has several **roots** (absolute folders); book paths are relative to their root (a moved disk changes
  one row). Stored full, without a trailing separator; must exist when added; never inside or around another root of
  the same tenant (a book would be two). Other tenants may share a folder.
- **Who adds what:** server administrators any folder the server reads; tenant administrators (`tenant.libraries`)
  only under `Librarion:Libraries:Folders` (empty by default = they can't add roots). Without this, a tenant
  administrator could point a library at another tenant's books or the server's own files.
- Removing a root is a task (`RemoveRoot`): files, archives and empty books go in batches, counts move, the row last.
  Interrupted: the root stays; remove it again.
- Routes: `GET|POST api/libraries/{id}/roots`, `DELETE …/roots/{rootId}` (202 with the run id); security log
  `LibraryChanged` with the path.

## Runs

- `BackgroundTasks`: one run per library at a time (a second start answers the running one, `started: false`); a root
  removal waits for the library's scan; at most `Librarion:Libraries:MaxParallelScans` (2) at once on the server, the
  rest wait (`waiting: true`).
- **Tenant runs** (stage 6: `TagDelete`, `TagMerge`, `FieldMerge`, `MergeUndo`; `StartTenantAsync`) have no library:
  one waits for every run of the tenant started before it, and every run started after it waits for it, so a scan
  never links a tag a merge is taking away. Listed (`running`, history, `api/tasks/{id}`) for those with
  `tenant.tags`; progress `changed` / `removed` count links, `current` names the tag.
- Each run is a `TaskRuns` row written at start and completed at the end: outcome (`succeeded`, `failed`,
  `cancelled`, `interrupted`, `skipped`), counts as JSON (added, changed, removed, unchanged, archives read / skipped,
  unreadable entries, missing roots, the first 20 problems). A failure's summary says to look in the server log; the
  exception is logged, never shown.
- Progress lives in memory (files seen, archives done, added / changed / removed, the current item) and is merged into
  the run while it goes. Cancel: the scan stops between batches, what it wrote stays (resumable: an archive is marked
  done only at its end).
- Start-up (`TaskScheduleWorker`): runs still `running` become `interrupted`; stopping the server cancels the runs and
  waits 5 s.
- Routes (right `library.scan` in that library, own tenant): `POST api/libraries/{id}/scan` (202),
  `GET api/libraries/{id}/tasks?before&limit` (newest first, keyset by id), `GET api/tasks/{run}`,
  `POST api/tasks/{run}/cancel`.

## Schedules

- A schedule says what it runs first (D-5.10, the user's point of 2026-11-01): `task` `libraryScan`, or `import` with
  `importKind`, `source` and `files`. Saving an import schedule runs `ImportRequests.CheckAsync` (the checks of
  `POST …/import`, the right Import too; its files folder becomes a root then, as a manual import's does). When due,
  `ImportRequests.StartScheduledAsync` checks again but never adds a root back (a removed one would come back by
  itself): source or folder gone → a `failed` run saying why; the library busy → `skipped` (`RecordUnstartedAsync`).
- Client: `admin/schedules-tab.ts` (the library's Schedules tab): rows "task · when · next" with the source of an
  import and an on/off switch (`role=switch` over a checkbox, `PUT` with the same values), Edit and Delete (asks); a
  dialog (`Sheet`) in numbered steps: what it runs (choice cards: Folder scan, Import from <kind> for each kind the
  account may import), where the import comes from, when.
- `TaskSchedules` rows per library: `interval` (≥ 15 minutes from `start`) or `weekly` (`days` bit 0 = Sunday, at
  `start`'s time of day in the server's time zone). Next run is computed by `ScheduleCalculator` (strictly after a
  moment; skipped DST times run later, repeated ones the first time).
- The worker checks every 30 s: an occurrence in (last check, now], never before the schedule's last change (no runs
  for the past), missed ones not caught up; due while the library's run goes → a `skipped` run.
- Routes: `GET|POST api/libraries/{id}/schedules`, `PUT|DELETE …/schedules/{scheduleId}`.
- A scan never removes a book: files it doesn't find are marked missing, books whose files are all gone become missing
  (hidden, kept) and come back when a file does ([missing-books](missing-books.md)). Their removal is a run of its own
  (`RemoveMissing`), started only by a user from the library's Missing books tab.
