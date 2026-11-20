// E-reader mode as an e-ink browser sees it (stage 8, agents/docs/features/ereader-mode.md): the `ereader` project, a
// Kindle's User-Agent at 600 × 800 with script off. The app's address sends it to /e/ (D-8.11); it signs in by the
// plain form (Stay signed in ticked), finds its library, searches, opens a book whose FB2 is inside a ZIP (the
// device's note, the FB2 dimmed for a Kindle), downloads it as it is and zipped, walks All books by title to its next
// page and back to the first, downloads a loose book, signs out, then browses and downloads with a key in the address
// alone. Every screen: no <script>, no colour, it fits, targets ≥ 44 px (D-8.9). The guard fails on any request to
// another origin. Then axe AA on each screen, with script on: the pages have none of their own, axe needs it.
import fs from 'node:fs';
import path from 'node:path';
import type { Download, Page } from '@playwright/test';
import { makeAccount, uniqueName, type Account } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { expectFitsScreen, expectNothingCut, expectTapTargets } from '../fixtures/layout';
import { addedLibrary } from '../fixtures/library';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { signedOut } from '../paths';

/** 25 loose FB2s (Book 01 … Book 25) and one inside pack.zip (Zipped): 26 books, two pages of 20 by title. */
function writeBooks(folder: string, suffix: string): { loose: Buffer; zipped: Buffer } {
    const author = `Kay Ink${suffix}`;
    let loose: Buffer | undefined;
    for (let i = 1; i <= 25; i++) {
        const number = String(i).padStart(2, '0');
        const data = fb2(`Book ${number} ${suffix}`, author);
        fs.writeFileSync(path.join(folder, `book-${number}.fb2`), data);
        loose ??= data;
    }
    const zipped = fb2(`Zipped ${suffix}`, author);
    writeZip(path.join(folder, 'pack.zip'), [{ name: 'zipped.fb2', data: zipped }]);
    return { loose: loose!, zipped };
}

/** A library of the test's own and an account (the Users group) to read it with. */
async function setUp(prefix: string, testInfo: Parameters<Parameters<typeof test>[2]>[1]) {
    const name = uniqueName(prefix, testInfo);
    const suffix = name.slice(name.lastIndexOf('-') + 1);
    let books!: { loose: Buffer; zipped: Buffer };
    const libraryId = await addedLibrary(name, folder => { books = writeBooks(folder, suffix); });
    const account = await makeAccount(prefix.toLowerCase(), testInfo);
    return { name, suffix, libraryId, account, books };
}

/** What every e-reader screen keeps to: no script, grayscale only, fits the screen, nothing cut, targets ≥ 44 px. */
async function expectPlainScreen(page: Page, screen: string): Promise<void> {
    await test.step(`${screen}: plain, grayscale, fits`, async () => {
        expect(await page.locator('script').count(), `${screen}: <script>`).toBe(0);
        const colours = await page.evaluate(() => {
            const found = new Set<string>();
            for (const element of [document.documentElement, ...document.querySelectorAll('body, body *')]) {
                const style = getComputedStyle(element);
                for (const value of [style.color, style.backgroundColor, style.borderTopColor, style.borderBottomColor, style.backgroundImage])
                    for (const [whole, r, g, b] of value.matchAll(/rgba?\((\d+), (\d+), (\d+)/g))
                        if (r !== g || g !== b)
                            found.add(`${element.tagName.toLowerCase()}.${element.className}: ${whole}`);
            }
            return [...found];
        });
        expect(colours, `${screen}: colours that aren't gray`).toEqual([]);
        await expectFitsScreen(page);
        await expectNothingCut(page);
        await expectTapTargets(page, 'body', 44);
    });
}

async function signIn(page: Page, account: Account): Promise<void> {
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');
    await page.getByLabel('Name', { exact: true }).fill(account.userName);
    await page.getByLabel('Password', { exact: true }).fill(account.password);
    await page.getByRole('button', { name: 'Sign in' }).tap();
}

/** Taps a download link and reads what came. */
async function download(page: Page, link: ReturnType<Page['getByRole']>): Promise<{ download: Download; body: Buffer }> {
    const coming = page.waitForEvent('download');
    await link.tap();
    const done = await coming;
    return { download: done, body: fs.readFileSync((await done.path())!) };
}

test.describe('@ereader', () => {
    test.use({ storageState: signedOut });

    test('a Kindle without script signs in, searches, downloads, pages, signs out and uses a key', async ({ page }, testInfo) => {
        const { name, suffix, account, books } = await setUp('EReader', testInfo);

        // The app's address, from a Kindle: the e-reader's sign-in page.
        await page.goto('./');
        await expect(page).toHaveURL(/\/e\/signin/);
        await expect(page.getByLabel('Keep me signed in on this device')).toBeChecked();
        await expectPlainScreen(page, 'sign-in');
        await signIn(page, account);

        // Its libraries (the run's others are there too), then this one.
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Libraries');
        await page.getByRole('link', { name, exact: true }).tap();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
        await expect(page.getByRole('heading', { name: 'Recently added' })).toBeVisible();
        await expectPlainScreen(page, 'library');

        // Search: the book inside the ZIP.
        await page.getByLabel('Search titles, authors, series').fill(`zipped ${suffix}`);
        await page.getByRole('button', { name: 'Search', exact: true }).tap();
        await expect(page).toHaveURL(/\/search\?q=/);
        const found = page.locator('ul.books > li');
        await expect(found).toHaveCount(1);
        await expectPlainScreen(page, 'search');
        await found.getByRole('link').tap();

        // Its page: ordered for a Kindle, which opens no FB2: the file under "Other formats", still downloadable.
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(`Zipped ${suffix}`);
        await expect(page.getByText('Formats ordered for Kindle.')).toBeVisible();
        await expect(page.getByText('Other formats: Kindle may not open them.')).toBeVisible();
        await expect(page.getByText('in pack.zip')).toBeVisible();
        await expectPlainScreen(page, 'book');
        const inZip = await download(page, page.getByRole('link', { name: 'Download FB2', exact: true }));
        expect(Buffer.compare(inZip.body, books.zipped), 'read from inside pack.zip').toBe(0);
        expect(inZip.download.suggestedFilename()).toMatch(/\.fb2$/);
        const zippedOnTheFly = await download(page, page.getByRole('link', { name: 'Download FB2, zipped' }));
        expect(zippedOnTheFly.body.subarray(0, 2).toString('latin1')).toBe('PK');

        // All books by title: 20, Next gives the other 6 (none twice), First goes back.
        await page.getByRole('link', { name, exact: true }).tap();
        await page.getByRole('link', { name: 'All books by title' }).tap();
        const rows = page.locator('ul.books .title');
        await expect(rows).toHaveCount(20);
        const first = await rows.allTextContents();
        expect(first[0]).toBe(`Book 01 ${suffix}`);
        await page.getByRole('link', { name: 'Next page' }).tap();
        await expect(rows).toHaveCount(6);
        const second = await rows.allTextContents();
        expect(new Set([...first, ...second]).size, 'every book once').toBe(26);
        await expect(page.getByRole('link', { name: 'Next page' })).toHaveCount(0);
        await expectPlainScreen(page, 'next page');
        await page.getByRole('link', { name: 'First page' }).tap();
        await expect(rows.first()).toHaveText(`Book 01 ${suffix}`);

        // A loose book, downloaded.
        await page.locator('ul.books > li').first().getByRole('link').tap();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(`Book 01 ${suffix}`);
        const loose = await download(page, page.getByRole('link', { name: 'Download FB2', exact: true }));
        expect(Buffer.compare(loose.body, books.loose)).toBe(0);

        // A key for the address, made on the account's API (the same session as the app's), before signing out.
        const made = await page.request.post('api/account/api-keys', {
            headers: { 'X-Librarion-Request': '1' }, data: { name: 'Kindle', scopes: ['opds'], pathAllowed: true },
        });
        expect(made.ok(), await made.text()).toBe(true);
        const key = (await made.json() as { key: string }).key;

        // Sign out (a form): the e-reader asks to sign in again.
        await page.getByRole('button', { name: 'Sign out' }).tap();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');
        await page.goto('e/');
        await expect(page).toHaveURL(/\/e\/signin/);
        expect((await page.context().cookies()).map(c => c.name)).not.toContain('librarion.session');

        // The key in the address alone: every link keeps it, down to a download from the ZIP.
        const prefix = `/e/k/${encodeURIComponent(key)}/`;
        await page.goto(prefix.slice(1));
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Libraries');
        await expect(page.getByRole('button', { name: 'Sign out' }), 'nothing to sign out of').toHaveCount(0);
        await page.getByRole('link', { name, exact: true }).tap();
        await page.getByLabel('Search titles, authors, series').fill(`zipped ${suffix}`);
        await page.getByRole('button', { name: 'Search', exact: true }).tap();
        await page.locator('ul.books > li').getByRole('link').tap();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(`Zipped ${suffix}`);
        expect(new URL(page.url()).pathname.startsWith(prefix), page.url()).toBe(true);
        const byKey = await download(page, page.getByRole('link', { name: 'Download FB2', exact: true }));
        expect(new URL(byKey.download.url()).pathname.startsWith(prefix)).toBe(true);
        expect(Buffer.compare(byKey.body, books.zipped)).toBe(0);
        await expectPlainScreen(page, 'book by key');
    });

    test.describe('with script on, for axe', () => {
        // The not-found page is a 404, which the browser logs.
        test.use({ javaScriptEnabled: true, allowedErrors: /no-such-page/ });

        test('every screen passes axe AA', async ({ page }, testInfo) => {
            const { name, suffix, account } = await setUp('EReaderA11y', testInfo);

            await page.goto('e/');
            await audit(page, 'sign-in');
            await signIn(page, account);
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Libraries');
            await audit(page, 'libraries');
            await page.getByRole('link', { name, exact: true }).tap();
            await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
            await audit(page, 'library');
            await page.getByLabel('Search titles, authors, series').fill(`book ${suffix}`);
            await page.getByRole('button', { name: 'Search', exact: true }).tap();
            await expect(page.getByRole('link', { name: 'Next page' })).toBeVisible();
            await audit(page, 'search');
            await page.getByRole('link', { name: 'Next page' }).tap();
            await expect(page.getByRole('link', { name: 'First page' })).toBeVisible();
            await audit(page, 'next page');
            await page.locator('ul.books > li').first().getByRole('link').tap();
            await expect(page.getByText('Formats ordered for Kindle.')).toBeVisible();
            await audit(page, 'book');
            await page.getByRole('link', { name: 'Change' }).tap();
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Formats for this device');
            await audit(page, 'device');
            await page.getByRole('link', { name: 'Librarion', exact: true }).tap();
            await page.getByRole('link', { name, exact: true }).tap();
            await page.getByRole('link', { name: 'Authors' }).tap();
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Authors');
            await audit(page, 'field values');
            await page.goto('e/no-such-page');
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Not found');
            await audit(page, 'not found');
        });
    });
});
