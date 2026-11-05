// Imports from other programs, on every device: the administrator imports a small Calibre library and a small INPX
// collection (both written by the test) into a new library from the library panel, sees each run with its counts, and
// downloads a book of each (the book page comes with stage 5; the API stands in for it).
import fs from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import type { Page } from '@playwright/test';
import { uniqueName } from '../fixtures/accounts';
import { libraryTab } from '../fixtures/admin';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { work } from '../paths';

/** A Calibre library of one book (an EPUB) with Calibre's own tables; the path of its metadata.db. */
function calibreLibrary(folder: string): string {
    fs.mkdirSync(path.join(folder, 'Ursula K. Le Guin', 'A Wizard of Earthsea (1)'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'Ursula K. Le Guin', 'A Wizard of Earthsea (1)', 'A Wizard of Earthsea - Ursula K. Le Guin.epub'), 'epub bytes');
    const file = path.join(folder, 'metadata.db');
    const db = new DatabaseSync(file);
    db.exec(`
        CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT NOT NULL, sort TEXT, pubdate TIMESTAMP, series_index REAL NOT NULL DEFAULT 1.0, path TEXT NOT NULL,
            uuid TEXT, has_cover BOOL DEFAULT 0);
        CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
        CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL);
        CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL, uncompressed_size INTEGER NOT NULL, name TEXT NOT NULL);
        INSERT INTO books VALUES (1, 'A Wizard of Earthsea', 'Wizard of Earthsea, A', '1968-01-01 00:00:00+00:00', 1, 'Ursula K. Le Guin/A Wizard of Earthsea (1)', 'uuid-1', 0);
        INSERT INTO authors VALUES (1, 'Ursula K. Le Guin', 'Le Guin, Ursula K.');
        INSERT INTO books_authors_link (book, author) VALUES (1, 1);
        INSERT INTO data (book, format, uncompressed_size, name) VALUES (1, 'EPUB', 10, 'A Wizard of Earthsea - Ursula K. Le Guin');
    `);
    db.close();
    return file;
}

/** An INPX of two records (one deleted) and its zip; the path of the .inpx. */
function inpxCollection(folder: string, book: Buffer): string {
    writeZip(path.join(folder, 'fb2-000001-000002.zip'), [{ name: '1.fb2', data: book }, { name: '2.fb2', data: fb2('Deleted', 'Nobody') }]);
    const line = (...fields: string[]) => fields.join('\x04') + '\x04';
    const inp = [
        line('Strugatsky,Arkady,:Strugatsky,Boris,:', 'sf:', 'Roadside Picnic', '', '', '1', '1', '1', '0', 'fb2', '2010-01-01', 'ru', '5', ''),
        line('Nobody,,:', 'prose:', 'Deleted', '', '', '2', '1', '2', '1', 'fb2', '2010-01-01', 'ru', '', ''),
    ].join('\r\n');
    const file = path.join(folder, 'collection.inpx');
    writeZip(file, [{ name: 'collection.info', data: Buffer.from('E2E collection\n') }, { name: 'fb2-000001-000002.inp', data: Buffer.from(inp, 'utf8') }]);
    return file;
}

async function importFrom(page: Page, isMobile: boolean, kind: string, source: string): Promise<void> {
    await press(libraryTab(page, 'Import'), isMobile);
    await page.getByLabel('Kind of source').selectOption({ label: kind });
    await page.getByLabel('Path of the source on the server').fill(source);
    await press(page.getByRole('button', { name: 'Import', exact: true }), isMobile);
    await press(libraryTab(page, 'Runs'), isMobile);
    const run = page.getByRole('list', { name: 'Runs' }).getByRole('listitem').first();
    await expect(run).toContainText('Import from another source');
    await expect(run).toContainText('done', { timeout: 30_000 });
}

test.describe('@library', () => {
    test('a Calibre library and an INPX collection are imported from the library panel @desktop', async ({ page, isMobile }, testInfo) => {
        const libraryName = uniqueName('Imported', testInfo);
        const folder = path.join(work, 'imports', libraryName);
        fs.rmSync(folder, { recursive: true, force: true });
        const picnic = fb2('Roadside Picnic', 'Arkady Strugatsky');
        const calibre = calibreLibrary(path.join(folder, 'Calibre Library'));
        const inpx = inpxCollection(path.join(folder, 'lib.rus.ec'), picnic);

        await page.goto('./admin/libraries');
        await page.getByLabel('Name of a new library').fill(libraryName);
        await press(page.getByRole('button', { name: 'Add', exact: true }), isMobile);
        await press(page.getByRole('link', { name: libraryName }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(libraryName);

        await importFrom(page, isMobile, 'Calibre library', calibre);
        await expect(page.getByRole('list', { name: 'Runs' }).getByRole('listitem').first()).toContainText('1 added');
        await press(libraryTab(page, 'Folders'), isMobile);
        await expect(page.getByRole('list', { name: 'Folders' }).getByText(path.dirname(calibre))).toBeVisible();

        await importFrom(page, isMobile, 'INPX catalog', inpx);
        await expect(page.getByRole('list', { name: 'Runs' }).getByRole('listitem').first()).toContainText('1 added · 0 updated · 0 unchanged · 1 skipped');

        const api = page.request;
        const headers = { 'X-Librarion-Request': '1' };
        const libraries = await (await api.get('api/libraries', { headers })).json() as { id: number; name: string }[];
        const library = libraries.find(each => each.name === libraryName)!;
        const books = await (await api.get(`api/books?library=${library.id}&sort=title`, { headers })).json() as { items: { id: number; title: string }[] };
        expect(books.items.map(book => book.title)).toEqual(['Roadside Picnic', 'A Wizard of Earthsea']);
        for (const [title, bytes] of [['A Wizard of Earthsea', Buffer.from('epub bytes')], ['Roadside Picnic', picnic]] as const) {
            const book = books.items.find(each => each.title === title)!;
            const detail = await (await api.get(`api/books/${book.id}`, { headers })).json() as { files: { id: number }[] };
            const download = await api.get(`api/books/${book.id}/files/${detail.files[0].id}`, { headers });
            expect(download.status()).toBe(200);
            expect(Buffer.compare(await download.body(), bytes)).toBe(0);
        }
    });
});
