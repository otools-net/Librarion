// Librarion end-to-end tests (README.md): the real app, built and started by scripts/server.mjs on a fresh database,
// driven in phone-sized browsers with touch, and on a desktop. Reports and traces go to the work folder, not the repo.
import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';
import { baseURL, here, work } from './paths';

// Chromium: the installed Edge on Windows (no download), Playwright's own elsewhere (CI). WebKit: `npm run browsers`.
const channel = process.env.LIBRARION_E2E_CHANNEL ?? (process.platform === 'win32' ? 'msedge' : undefined);
const android = { ...devices['Pixel 7'], channel };

type Options = { allowedErrors: RegExp | null; layoutCheck: boolean };

export default defineConfig<Options>({
    testDir: path.join(here, 'specs'),
    outputDir: path.join(work, 'results'),
    fullyParallel: true,
    forbidOnly: !!process.env.CI,
    retries: process.env.CI ? 1 : 0,
    workers: process.env.LIBRARION_E2E_WORKERS ? Number(process.env.LIBRARION_E2E_WORKERS) : '50%',
    timeout: 60_000,
    expect: { timeout: 10_000 },
    reporter: [['list'], ['html', { outputFolder: path.join(work, 'report'), open: 'never' }]],
    use: {
        baseURL,
        colorScheme: 'dark',
        trace: 'retain-on-failure',
        video: 'retain-on-failure',
        screenshot: 'only-on-failure',
    },
    // Phones first (priority 3, agents/docs/development/frontend.md); `@desktop` specs also run on a desktop, phone
    // specs never do there.
    projects: [
        { name: 'android', use: android, grepInvert: /@desktop-only/ },
        {
            name: 'android-small',
            use: { ...android, viewport: { width: 360, height: 740 }, screen: { width: 360, height: 800 }, deviceScaleFactor: 3 },
            grepInvert: /@desktop-only/,
        },
        { name: 'iphone', use: { ...devices['iPhone 13'] }, grepInvert: /@desktop-only/ },
        {
            name: 'landscape',
            use: { ...android, viewport: { width: 800, height: 360 }, screen: { width: 800, height: 412 } },
            grepInvert: /@desktop-only/,
        },
        { name: 'desktop', use: { ...devices['Desktop Chrome'], channel, viewport: { width: 1280, height: 800 } }, grep: /@desktop/ },
    ],
    webServer: {
        command: 'node scripts/server.mjs',
        cwd: here,
        url: `${baseURL}api/system/info`,
        // The first run builds the app (client included): minutes.
        timeout: 15 * 60_000,
        reuseExistingServer: false,
        stdout: 'pipe',
        stderr: 'pipe',
    },
});
