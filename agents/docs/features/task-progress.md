# Task progress: the panel and Background tasks

Code: `core/task-watch.ts` (`TaskWatch`), `core/task-text.ts` (titles, counts, elapsed, relative times),
`shared/task-panel.ts` (`TaskPanel`, in `shell/shell.html` at the top of `main`), `admin/tasks-section.ts`
(Administration › Background tasks), `admin/run-list.ts` (runs as rows, shared with a library's Runs tab); styles
`styles/tasks.css`. Server: `GET api/tasks/running`, `api/tasks/overview` (`TaskOverview`), `api/tasks` (history) in
`Api/Endpoints/TaskEndpoints.cs`; `BackgroundTasks.RunningAsync`, `LastAsync`, `HistoryAsync`, `TaskSchedules.ListAsync`
(libraries). Tests `tasks.spec.ts`, `TaskApiTests.TheOverviewAndHistoryCoverTheLibrariesTheCallerMayScanOnly`, e2e
`library.spec.ts`. Port of SFE's scan panel and Background tasks section (D-5.11 under
[Decisions](#decisions-stage-5-confirmed-by-the-user-2026-11-01)); the runs themselves: [tasks](tasks.md).

- Everything covers the libraries the caller may scan (`Access.LibrariesWith(Rights.Scan)`); the overview also lists
  their ids (`libraries`), as the client has no per-library rights. Tenant runs (tag deletes, merges, undos: no
  library) are added for `tenant.tags` (stage 6): the watch runs for `library.scan` or `tenant.tags`, the run's place
  reads "Every library" and its counts are links (`task-text.ts`).
- `api/tasks/running` answers from `BackgroundTasks.active` (memory) and reads the database only when something runs
  in the tenant: polling it is free while idle. `TaskWatch` polls it every 1.5 s while a run goes, every 20 s
  otherwise, never while the page is hidden (asks at once when it shows again), and only while the panel is shown and
  the session holds `library.scan` somewhere.
- A run that leaves `running` is fetched (`api/tasks/{id}`) and kept in `ended` until dismissed (ids remembered in
  `librarion.tasks.dismissed`, the last 20), with a toast. `follow(runId)` (called by whatever starts a run: Scan now,
  an import, removals, Run now) makes its result show even when it ended before any poll saw it (a small library's scan
  takes under a second). A refresh asked while one is on its way runs right after it (`again`).
- `BackgroundTasks.RunAsync` writes a run's outcome before taking it out of `active`: the other way round, a run gone
  from `api/tasks/running` could still read `running` from `api/tasks/{id}` and the panel dropped it (seen once in
  e2e). The client also asks again, with the next poll, for a run that still reads `running`.
- The panel: sticky under the top bar across the page (the page's padding undone; none in the administration's
  frame), one row per run: icon, "Scanning Comics…" (accent; red when failed), elapsed (ticking each second while
  running), the counts so far, the file being read, Cancel (asks first) or, once ended, its outcome and Dismiss. Its
  height is `--task-panel-h` (ResizeObserver): the administration's column sticks below it. Other people's and
  schedules' runs show too (the e2e devices share a server: tests filter rows by their library).
- Background tasks (group Tenant and server, `admin/tasks/{tasks,schedules,history}`, right `library.scan`):
  - Tasks: a summary line ("Running: … · next: …"), a card per kind (Folder scan; Import with `library.import`): icon
    tile, state (Idle / Running · elapsed with a spinner), the library it acts on (a select when several), Run now
    (scan) or Start an import (the library's Import tab), Schedules; the runs going on with a 6 px bar, the counts and
    the current file, Cancel; Last run (outcome, time, library; links to History) and Next run (the first enabled
    schedule of that kind: time, "in 5 hours", library). Refreshed every 2 s while shown and visible, and when the
    watch's runs change; an answer overtaken by a later one is dropped.
  - Schedules: each library's own `SchedulesTab` under its name (add, edit, switch as on the library's tab).
  - History: every library's runs, newest first, 20 at a time ("Older runs" by key), the library linking to its Runs.
- Two cards, not three: runs don't record which import kind they ran (the schedules do); a run's kind would need a
  column. Removals have no card: they show in the panel and the history.
- The bars are indeterminate (a scan's total is unknown until it ends), moved by `transform`; reduced motion shows them
  still.
- Times: on SQLite every `DateTime` is read back as UTC (`SqliteLibrarionDbContext.ConfigureConventions`), so JSON
  carries its `Z`; before, browsers read run times as local and the elapsed clock was off by the zone.

## Decisions (stage 5, confirmed by the user 2026-11-01)

- **D-5.11 Scan progress as SFE's scan panel (the user, 2026-11-02):** while a library is scanned or imported the
  user sees it going and what it found so far, as SFE's `ScanPanelComponent`: a panel sticky under the top bar
  (`aria-live="polite"`), a thin indeterminate bar (moved by `transform`; reduced motion honoured), a title, the
  elapsed time (ticking only while running and the page is visible), the counts so far and the file being read,
  Cancel while it runs; afterwards the result stays until dismissed (remembered per browser by the run's start) and a
  toast says it finished. Seen from the library pages and while browsing, not only the library's admin tab.
- **Also SFE's Background tasks (the user, 2026-11-02):** a section under Server with Tasks, Schedules and History
  (chips with badges); a summary line; a card per task kind (folder scan, Calibre import, INPX import) with its state,
  Cancel / Run now / Schedule, progress and last and next runs; schedules of every library; the history newest first
  across libraries; refreshed every 2 s while shown and visible. Server: one overview route bounded by the libraries,
  not the catalog, and the history as a keyset page; only libraries where the account may scan (or import).
