// Screenshots of the app's pages on a phone (360 px) and a desktop (1280 px), to look at a layout by eye: starts the
// e2e server (tests/e2e/scripts/server.mjs, fresh database), sets it up through the API, signs in as its
// administrator and saves one PNG per page and size. Uses the e2e tests' Playwright (`npm ci` in tests/e2e first).
//   node agents/tools/screenshots/screenshots.mjs [out folder] [page ...]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// LIBRARION_SCREENS_THEME=light for the light colors (default dark).
const theme = process.env.LIBRARION_SCREENS_THEME === 'light' ? 'light' : 'dark';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const e2e = path.join(repo, 'tests/e2e');
const { chromium, devices, request } = createRequire(path.join(e2e, 'package.json'))('@playwright/test');

const [outArg, ...pageArgs] = process.argv.slice(2);
const out = path.resolve(outArg ?? path.join(os.tmpdir(), 'librarion-screens'));
const port = process.env.LIBRARION_E2E_PORT ?? '5192';
const baseURL = `http://localhost:${port}/`;
const pages = pageArgs.length ? pageArgs : [
    'login', 'login#recover', '', 'account', 'admin/users', 'admin/users#new', 'admin/groups', 'admin/groups#new',
    'admin/libraries', 'admin/libraries#open', 'admin/tenants', 'admin/tenants#edit', 'admin/settings', 'admin/security', 'missing',
];
const sizes = {
    phone: { ...devices['Pixel 7'], viewport: { width: 360, height: 740 }, screen: { width: 360, height: 800 } },
    desktop: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
};
const channel = process.env.LIBRARION_E2E_CHANNEL ?? (process.platform === 'win32' ? 'msedge' : undefined);
const admin = { userName: 'admin', password: 'admin-password-1' };

// Its own work folder and port: a run of the e2e tests can go on beside it.
const work = path.join(os.tmpdir(), 'librarion-screens-server');
fs.mkdirSync(work, { recursive: true });
const server = spawn(process.execPath, [path.join(e2e, 'scripts/server.mjs')], {
    stdio: 'inherit',
    env: {
        ...process.env, LIBRARION_E2E_PORT: port, LIBRARION_E2E_WORK: work,
        LIBRARION_E2E_SKIP_BUILD: process.env.LIBRARION_E2E_SKIP_BUILD ?? '1',
    },
});
const stop = () => server.kill();
process.on('exit', stop);

try {
    for (let i = 0; ; i++) {
        const ready = await fetch(`${baseURL}api/setup`).then(r => r.ok, () => false);
        if (ready)
            break;
        if (i > 600)
            throw new Error('the server did not start in 5 minutes');
        await new Promise(resolve => setTimeout(resolve, 500));
    }

    const api = await request.newContext({ baseURL, extraHTTPHeaders: { 'X-Librarion-Request': '1' } });
    const setup = await api.post('api/setup', { data: { code: 'e2e-setup-code-1', ...admin, tenantName: 'Home', libraryName: 'Books' } });
    if (!setup.ok())
        throw new Error(`setup: ${setup.status()} ${await setup.text()}`);
    // A folder of a few books, added to the library and scanned: the library panel has folders, runs and progress.
    const books = path.join(work, 'books');
    fs.mkdirSync(books, { recursive: true });
    for (let i = 1; i <= 3; i++)
        fs.writeFileSync(path.join(books, `book-${i}.fb2`), `<?xml version="1.0"?><FictionBook><description><title-info><genre>sf_fantasy</genre>`
            + `<author><first-name>Ann</first-name><last-name>Writer</last-name></author><book-title>Book ${i}</book-title></title-info></description><body/></FictionBook>`);
    const [library] = await (await api.get('api/libraries')).json();
    await api.post(`api/libraries/${library.id}/roots`, { data: { path: books } });
    await api.post(`api/libraries/${library.id}/scan`);
    // A second library: the top bar shows its switcher.
    await api.post('api/libraries', { data: { name: 'Comics' } });
    const signedIn = await api.storageState();
    await api.dispose();

    fs.mkdirSync(out, { recursive: true });
    const browser = await chromium.launch({ channel });
    for (const [size, device] of Object.entries(sizes)) {
        for (const address of pages) {
            const [route, action] = address.split('#');
            const signedOut = route === 'login';
            const context = await browser.newContext({ ...device, baseURL, colorScheme: theme, storageState: signedOut ? undefined : signedIn });
            const page = await context.newPage();
            await page.goto(`./${route}`);
            await page.getByRole('heading', { level: 1 }).waitFor();
            if (action === 'recover')
                await page.getByRole('link', { name: 'Recover the account' }).click();
            else if (action === 'new')
                await page.getByRole('button', { name: /^New / }).click();
            else if (action === 'edit')
                await page.getByRole('button', { name: 'Edit' }).first().click();
            else if (action === 'open')
                await page.locator('.row-main').first().click();
            else if (action === 'delete')
                await page.getByRole('button', { name: 'Delete' }).first().click();
            else if (action === 'account')
                await page.locator('.account-button').click();
            else if (action === 'library' && size === 'desktop')
                await page.locator('.switch-button').click();
            else if (action === 'menu' && size === 'phone')
                await page.locator('.menu-button').click();
            else if (action === 'list')
                await page.getByRole('button', { name: 'List' }).click();
            else if (action === 'scan') {
                await page.getByRole('button', { name: 'Run now' }).first().click();
                await page.locator('lib-task-panel .task-row').first().waitFor();
            } else if (action?.startsWith('search=') || action?.startsWith('found=')) {
                await page.locator('.admin-search input').fill(decodeURIComponent(action.slice(action.indexOf('=') + 1)));
                if (action.startsWith('found='))
                    await page.locator('.admin-search-results a').first().click();
            }
            await page.waitForLoadState('networkidle');
            // Dialogs and sheets rise in; the shot waits until they stand still.
            await page.waitForTimeout(400);
            const file = path.join(out, `${size}-${(address || 'home').replaceAll(/[/#]/g, '-')}${theme === 'light' ? '-light' : ''}.png`);
            await page.screenshot({ path: file, fullPage: !action });
            console.log(file);
            await context.close();
        }
    }
    await browser.close();
} finally {
    stop();
}
