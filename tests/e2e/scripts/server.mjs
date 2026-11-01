// Started by Playwright (`webServer` in playwright.config.ts), stopped by it after the run (as SFE):
//   build Librarion.Api (unless LIBRARION_E2E_SKIP_BUILD=1 and a build is there) → the app on a fresh SQLite database
//   with a known setup code (paths.ts), everything under the work folder (LIBRARION_E2E_WORK, default
//   <tmp>/librarion-e2e): its data folder too, never the repo's `src/Librarion.Api/data` (the content root).
// Runs in the foreground until killed; the app's output goes to <work>/app.log.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const work = path.resolve(process.env.LIBRARION_E2E_WORK ?? path.join(os.tmpdir(), 'librarion-e2e'));
const port = process.env.LIBRARION_E2E_PORT ?? '5191';
const build = path.join(work, 'build');
const data = path.join(work, 'data');
const dll = path.join(build, 'bin/Librarion.Api/debug/Librarion.Api.dll');

if (process.env.LIBRARION_E2E_SKIP_BUILD !== '1' || !fs.existsSync(dll)) {
    console.log(`[e2e] building Librarion.Api into ${build}`);
    const result = spawnSync('dotnet', ['build', path.join(repo, 'src/Librarion.Api'), '--artifacts-path', build, '-v', 'q', '-nologo'],
        { stdio: 'inherit' });
    if (result.status !== 0) {
        console.error('[e2e] build failed');
        process.exit(1);
    }
}

fs.rmSync(data, { recursive: true, force: true });
fs.mkdirSync(data, { recursive: true });

const log = fs.openSync(path.join(work, 'app.log'), 'w');
const app = spawn('dotnet', [dll, '--contentRoot', path.join(repo, 'src/Librarion.Api')], {
    cwd: work,
    stdio: ['ignore', log, log],
    env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        Urls: `http://localhost:${port}`,
        Librarion__Database__Provider: 'sqlite',
        ConnectionStrings__Librarion: `Data Source=${path.join(data, 'librarion.db')}`,
        // As paths.ts `setupCode`.
        Librarion__Setup__Code: 'e2e-setup-code-1',
        Librarion__Setup__CodeFile: path.join(data, 'setup-code.txt'),
        Librarion__Security__KeysDirectory: path.join(data, 'keys'),
        // Every spec signs in from this one address.
        Librarion__Security__SignInsPerMinute: '10000',
    },
});
console.log(`[e2e] app on http://localhost:${port}/ (log ${path.join(work, 'app.log')})`);

const stop = () => { app.kill(); process.exit(0); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
app.on('exit', code => { console.error(`[e2e] the app stopped (${code}): see ${path.join(work, 'app.log')}`); process.exit(1); });
