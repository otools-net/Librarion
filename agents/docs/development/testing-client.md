# Client test gotchas (Vitest + jsdom)

Rules and helpers: [testing](testing.md). Code: `src/Librarion.Web/src/testing/setup.ts`, `fakes.ts`. Ported from
SFE's `testing-client.md`.

- Angular runs Vitest with `isolate: false`: spec files of a worker share globals, and `setup.ts` runs again for each
  file. It replaces what it installs as a whole (never `??=` next to its own state), and before each test clears
  storage, media query matches and listeners, and `data-theme`.
- Specs run in a zoneless TestBed: `await fixture.whenStable()` after an action; `await settle()` for promises the
  component started (an API answer).
- `mockApi()`: every `Api` method is a `vi.fn` returning a promise that never settles until the spec sets
  `mockResolvedValue` / `mockRejectedValue`.
- Services read `matchMedia` in their constructor: `setMediaQuery(...)` before `TestBed.inject`, and
  `TestBed.resetTestingModule()` to get a new instance.
- One spec file: `npm test -- --include src/app/shell/shell.spec.ts`; plain `npx vitest run` fails (no Angular build).
- Literal texts and missing translation keys are caught by the backend test `ClientI18nTests` (it reads the
  templates), not by a spec.
- Error codes the server sends are `field.code` (`path.notAllowed`, `file.gone`); `ClientI18nTests` collects them
  from the access services and from the endpoint files it lists (`TaskEndpoints.cs`, `BookEndpoints.cs`): a new
  endpoint file with codes is added to that list, and each code gets `error.<code>` in `en.json`.
