// Where a run keeps its things, shared by the config and the specs: nothing goes into the repo.
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.resolve(here, '../..');

/** Build, database, reports, traces: `LIBRARION_E2E_WORK`, else `<tmp>/librarion-e2e`. */
export const work = path.resolve(process.env.LIBRARION_E2E_WORK ?? path.join(os.tmpdir(), 'librarion-e2e'));
export const port = Number(process.env.LIBRARION_E2E_PORT ?? 5191);
export const baseURL = `http://localhost:${port}/`;
