// A book's page on every device: a loose FB2 and a ZIP of two (one the same work as the loose one), scanned through the
// API; a card opens the book's page (title, author, Download), the file inside the ZIP downloads as it is, the same
// work in the other file shows as a possible duplicate and its loose file downloads too; Back returns to the library.
import fs from 'node:fs';
import path from 'node:path';
import type { Download, Page } from '@playwright/test';
import { uniqueName } from '../fixtures/accounts';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';

async function downloaded(page: Page, start: () => Promise<void>): Promise<{ name: string; bytes: Buffer }> {
    const waiting = page.waitForEvent('download');
    await start();
    const download: Download = await waiting;
    return { name: download.suggestedFilename(), bytes: fs.readFileSync((await download.path())!) };
}

test.describe('@browse', () => {
    // scannedLibrary reloads the page while the progress panel's poll may be on its way: WebKit reports the fetch it
    // cuts off as an "access control" error.
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('a book\'s page shows it and downloads its files, loose or inside a ZIP @smoke @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Book page', testInfo);
        const author = `Frank Dunes${name.slice(name.lastIndexOf('-') + 1)}`;
        const loose = fb2('Sand Planet', author);
        const messiah = fb2('Sand Messiah', author);
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'Sand Planet.fb2'), loose);
            writeZip(path.join(folder, 'books.zip'), [
                { name: 'sub/Sand Messiah.fb2', data: messiah },
                { name: 'sub/planet-copy.fb2', data: Buffer.concat([loose, Buffer.from('\n')]) },
            ]);
        });

        // A card opens the book's page.
        await press(page.locator('.book-link', { hasText: 'Sand Messiah' }), isMobile);
        await expect(page).toHaveURL(/\/books\/\d+$/);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sand Messiah');
        await expect(page.locator('.book-by a')).toHaveText(author);
        await expect(page.locator('.book-page-cover img')).toHaveJSProperty('complete', true);
        await expect(page.locator('.book-file')).toContainText('in books.zip');

        // The entry comes out of the ZIP as it went in.
        const entry = await downloaded(page, () => press(page.getByRole('link', { name: 'Download FB2' }), isMobile));
        expect(entry.name).toBe('Sand Messiah.fb2');
        expect(entry.bytes.equals(messiah)).toBe(true);

        // Back to the library, then the planet in the ZIP: the loose one is the same work.
        await press(page.getByRole('button', { name: 'Back' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
        const planets = page.locator('.book-link', { hasText: 'Sand Planet' });
        await expect(planets).toHaveCount(2);
        await press(planets.first(), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sand Planet');
        if (!(await page.locator('.book-file').textContent())?.includes('books.zip')) {
            // The first card was the loose one: its duplicate is the copy in the ZIP; go there and come back from it.
            await press(page.locator('.book-duplicate'), isMobile);
            await expect(page.locator('.book-file')).toContainText('in books.zip');
        }
        const duplicate = page.locator('.book-duplicate');
        await expect(duplicate.locator('.book-reason')).toHaveText('Same title and author');
        await press(duplicate, isMobile);
        await expect(page.locator('.book-file')).not.toContainText('books.zip');
        const file = await downloaded(page, () => press(page.getByRole('link', { name: 'Download Sand Planet.fb2' }), isMobile));
        expect(file.name).toBe('Sand Planet.fb2');
        expect(file.bytes.equals(loose)).toBe(true);
    });
});
