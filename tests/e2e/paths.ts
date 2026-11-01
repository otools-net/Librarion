// Where a run keeps its things, shared by the config, the server script and the specs: nothing goes into the repo.
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.resolve(here, '../..');

/** Build, database, sign-in states, reports, traces: `LIBRARION_E2E_WORK`, else `<tmp>/librarion-e2e`. */
export const work = path.resolve(process.env.LIBRARION_E2E_WORK ?? path.join(os.tmpdir(), 'librarion-e2e'));
export const port = Number(process.env.LIBRARION_E2E_PORT ?? 5191);
export const baseURL = `http://localhost:${port}/`;

/** The app's data folder (scripts/server.mjs): database, keys, `recovery-code.txt`. */
export const data = path.join(work, 'data');
/** As `Librarion__Setup__Code` in scripts/server.mjs. */
export const setupCode = 'e2e-setup-code-1';

/** The server administrator the first start makes (`specs/first-start.setup.ts`); other accounts are made per test. */
export const admin = { userName: 'admin', password: 'admin-password-1', tenant: 'Home', library: 'Books' } as const;
/** Its signed-in browser state, the specs' default (`storageState` in playwright.config.ts). */
export const adminState = path.join(work, 'auth', 'admin.json');
/** No cookies, nothing stored: `test.use({ storageState: signedOut })`. */
export const signedOut = { cookies: [], origins: [] };
