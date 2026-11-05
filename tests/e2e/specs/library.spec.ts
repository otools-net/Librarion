// A library from folder to download, on every device: the administrator adds a folder holding a ZIP of books to a new
// library, scans it from the library panel, sees the run with its counts, then downloads a book straight from the zip
// and gets its cover (the book page comes with stage 5; the API stands in for it).
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { libraryTab } from '../fixtures/admin';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { work } from '../paths';

test.describe('@library', () => {
    test('a folder with a zip is scanned and its books download from inside the zip @desktop', async ({ page, isMobile }, testInfo) => {
        const libraryName = uniqueName('Zipped', testInfo);
        const folder = path.join(work, 'books', libraryName);
        fs.rmSync(folder, { recursive: true, force: true });
        const books = [
            { name: 'fb2-000001.fb2', data: fb2('The Colour of Magic', 'Terry Pratchett') },
            { name: 'fb2-000002.fb2', data: fb2('Equal Rites', 'Terry Pratchett') },
        ];
        writeZip(path.join(folder, 'lib.rus.ec', 'fb2-000001-000002.zip'), books);

        await page.goto('./admin/libraries');
        await page.getByLabel('Name of a new library').fill(libraryName);
        await press(page.getByRole('button', { name: 'Add', exact: true }), isMobile);
        await press(page.getByRole('link', { name: libraryName }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(libraryName);

        await page.getByLabel('Full path of a folder on the server').fill(folder);
        await press(page.getByRole('button', { name: 'Add folder' }), isMobile);
        await expect(page.getByRole('list', { name: 'Folders' }).getByText(folder)).toBeVisible();

        // The scan bar is over every tab: started from Folders, followed on Runs.
        await press(page.getByRole('button', { name: 'Scan now' }), isMobile);
        await press(libraryTab(page, 'Runs'), isMobile);
        const run = page.getByRole('list', { name: 'Runs' }).getByRole('listitem').first();
        await expect(run).toContainText('done', { timeout: 30_000 });
        await expect(run).toContainText('2 added');
        await expect(page.getByRole('button', { name: 'Scan now' })).toBeEnabled();

        // The panel under the top bar keeps the result until dismissed (other devices' runs on this server show too).
        const result = page.locator('lib-task-panel .task-row').filter({ hasText: `Scan · ${libraryName}` });
        await expect(result).toContainText('2 added');
        await press(result.getByRole('button', { name: 'Dismiss' }), isMobile);
        await expect(result).toHaveCount(0);

        // Background tasks: the scan card runs it again for the library chosen, its last run is this one.
        await page.goto('./admin/tasks');
        const card = page.getByRole('article', { name: 'Folder scan' });
        await card.getByRole('combobox', { name: 'Library' }).selectOption({ label: libraryName });
        await press(card.getByRole('button', { name: 'Run now' }), isMobile);
        await expect(result.locator('.badge')).toHaveText('done', { timeout: 30_000 });
        await press(page.getByRole('link', { name: 'History' }).or(page.getByRole('tab', { name: 'History' })).first(), isMobile);
        // Every library's runs (other devices' too): this library's two scans, linked to its runs.
        await expect(page.getByRole('list', { name: 'Runs' }).getByRole('link', { name: libraryName })).toHaveCount(2);

        // The page's own session (its cookies) asks the API, as the book page will.
        const api = page.request;
        const headers = { 'X-Librarion-Request': '1' };
        const libraries = await (await api.get('api/libraries', { headers })).json() as { id: number; name: string }[];
        const library = libraries.find(each => each.name === libraryName)!;
        const page1 = await (await api.get(`api/books?library=${library.id}&sort=title`, { headers })).json() as { items: { id: number; title: string }[] };
        // By sort title: the leading article doesn't count ("Colour of Magic" before "Equal Rites").
        expect(page1.items.map(book => book.title)).toEqual(['The Colour of Magic', 'Equal Rites']);
        const book = page1.items.find(each => each.title === 'Equal Rites')!;
        const detail = await (await api.get(`api/books/${book.id}`, { headers })).json() as { files: { id: number; name: string; inArchive: boolean }[] };
        expect(detail.files).toHaveLength(1);

        const download = await api.get(`api/books/${book.id}/files/${detail.files[0].id}`, { headers });
        expect(download.status()).toBe(200);
        expect(download.headers()['content-disposition']).toContain('fb2-000002.fb2');
        expect(Buffer.compare(await download.body(), books[1].data)).toBe(0);

        const cover = await api.get(`api/books/${book.id}/cover/m`, { headers });
        expect(cover.status()).toBe(200);
        expect(cover.headers()['content-type']).toBe('image/webp');
    });

    test('a schedule says what it runs: chosen first in its dialog, then when, and turned off by its switch @desktop', async ({ page, isMobile }, testInfo) => {
        const libraryName = uniqueName('Scheduled', testInfo);
        await page.goto('./admin/libraries');
        await page.getByLabel('Name of a new library').fill(libraryName);
        await press(page.getByRole('button', { name: 'Add', exact: true }), isMobile);
        await press(page.getByRole('link', { name: libraryName }), isMobile);
        await press(libraryTab(page, 'Schedules'), isMobile);

        await press(page.getByRole('button', { name: 'New schedule' }), isMobile);
        const dialog = page.getByRole('dialog', { name: 'New schedule' });
        await expect(dialog.getByRole('group', { name: 'What it runs' })).toBeVisible();
        await press(dialog.getByText('Folder scan', { exact: true }), isMobile);
        await press(dialog.getByRole('radio', { name: 'On days of the week' }), isMobile);
        await dialog.getByLabel('Time of day').fill('04:30');
        await press(dialog.getByRole('button', { name: 'Save' }), isMobile);
        await expect(dialog).toBeHidden();

        const row = page.getByRole('list', { name: 'Schedules' }).getByRole('listitem').first();
        await expect(row).toContainText('Folder scan');
        await expect(row).toContainText('next');
        const toggle = row.getByRole('switch', { name: 'Folder scan on' });
        await expect(toggle).toBeChecked();
        await press(toggle, isMobile);
        await expect(row).toContainText('off');
        await expect(toggle).not.toBeChecked();
    });
});
