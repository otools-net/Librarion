// The library on every device: 70 books in a zip, scanned through the API; the home page shows them as covers, loads the
// next page while scrolling, switches to the list and back, sorts, and comes back to where it was after another page.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import type { Locator } from '@playwright/test';
import { press } from '../fixtures/press';
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { work } from '../paths';

const headers = { 'X-Librarion-Request': '1' };

/**
 * Whether the element's box is within the window (by geometry: `toBeInViewport` reported a drawn, painted card
 * as out of view under mobile emulation once the list's space above it changed).
 */
async function onScreen(locator: Locator): Promise<boolean> {
    const box = await locator.boundingBox();
    const height = await locator.page().evaluate(() => innerHeight);
    return box !== null && box.y < height && box.y + box.height > 0;
}

test.describe('@browse', () => {
    // The test reloads the page while the progress panel's poll may be on its way: WebKit reports the fetch it cuts off
    // as an "access control" error.
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('the library shows its books, loads more while scrolling and comes back where it was @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Shelf of 70', testInfo);
        const folder = path.join(work, 'books', name);
        fs.rmSync(folder, { recursive: true, force: true });
        writeZip(path.join(folder, 'books.zip'), Array.from({ length: 70 }, (_, i) => ({
            name: `book-${String(i + 1).padStart(3, '0')}.fb2`,
            data: fb2(`Title ${String(i + 1).padStart(3, '0')}`, 'Ann Author'),
        })));

        // The library, its folder and a scan, through the page's own session.
        await page.goto('./');
        const api = page.request;
        const library = await (await api.post('api/libraries', { headers, data: { name } })).json() as { id: number };
        expect((await api.post(`api/libraries/${library.id}/roots`, { headers, data: { path: folder } })).ok()).toBe(true);
        const { runId } = await (await api.post(`api/libraries/${library.id}/scan`, { headers })).json() as { runId: number };
        await expect.poll(async () => (await (await api.get(`api/tasks/${runId}`, { headers })).json()).outcome, { timeout: 30_000 }).toBe('succeeded');

        // The library chosen (the switcher's choice is kept per browser).
        await page.evaluate(id => localStorage.setItem('librarion.library', String(id)), library.id);
        await page.goto('./');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
        await expect(page.locator('.browse-count')).toHaveText('70 books');
        const books = page.getByRole('list', { name: `Books of ${name}` });
        await expect(books.locator('.book').first()).toContainText('Title 001');

        // Scrolled to the end: the second page comes (50 a page), and only the rows in view are drawn.
        // (Scrolled by script: mobile WebKit has no wheel; again as each page makes the list longer.)
        const last = books.locator('.book[aria-posinset="70"]');
        await expect.poll(async () => {
            await page.evaluate(() => scrollTo({ top: document.documentElement.scrollHeight }));
            return last.count();
        }, { timeout: 15_000 }).toBe(1);
        await page.evaluate(() => scrollTo({ top: document.documentElement.scrollHeight }));
        await expect.poll(() => onScreen(last)).toBe(true);
        await expect(last).toContainText('Title 070');
        expect(await books.locator('.book').count()).toBeLessThan(70);

        // Where it was, after another page of the app and back (the list is kept by the open tab, not across reloads).
        const before = await page.evaluate(() => scrollY);
        if (isPhoneWidth(page))
            await page.locator('.menu-button').tap();
        await press(page.locator('#main-nav').getByRole('link', { name: 'Administration' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Administration');
        await page.goBack();
        await expect.poll(() => onScreen(last)).toBe(true);
        await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(before - 50);
        expect(Math.abs(await page.evaluate(() => scrollY) - before)).toBeLessThan(50);

        // The list, then newest first: both remembered.
        await press(page.getByRole('button', { name: 'List' }), isMobile);
        await expect(page.locator('.books.list')).toBeVisible();
        await page.getByRole('combobox', { name: 'Sort' }).selectOption('added');
        await expect(books.locator('.book').first()).toBeVisible();
        await page.reload();
        await expect(page.locator('.books.list')).toBeVisible();
        await expect(page.getByRole('combobox', { name: 'Sort' })).toHaveValue('added');
    });
});
