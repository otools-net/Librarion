// Browsing by value on every device: a library of four books (two authors, a series of three), scanned through the
// API; Authors lists its authors with their counts, "Go to" finds one, its page lists its books; a series lists its
// books by number; All fields leads to the lists.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { press } from '../fixtures/press';
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';
import { work } from '../paths';

const headers = { 'X-Librarion-Request': '1' };

test.describe('@browse', () => {
    test('authors and series list their values with counts, and each value its books @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Values', testInfo);
        // Names of this test only: the tenant's vocabulary is shared by every library of the run.
        const author = `Cleo Quill${name.slice(name.lastIndexOf('-') + 1)}`;
        const other = `Bob Inkwell${name.slice(name.lastIndexOf('-') + 1)}`;
        const series = `Saga ${name.slice(name.lastIndexOf('-') + 1)}`;
        const folder = path.join(work, 'books', name);
        fs.rmSync(folder, { recursive: true, force: true });
        fs.mkdirSync(folder, { recursive: true });
        // The series' files out of order: its page puts them in order by number.
        for (const [file, title, number] of [['a.fb2', 'Part Three', 3], ['b.fb2', 'Part One', 1], ['c.fb2', 'Part Two', 2]] as const)
            fs.writeFileSync(path.join(folder, file), fb2(title, author, { name: series, number }));
        fs.writeFileSync(path.join(folder, 'd.fb2'), fb2('Alone', other));

        await page.goto('./');
        const api = page.request;
        const library = await (await api.post('api/libraries', { headers, data: { name } })).json() as { id: number };
        expect((await api.post(`api/libraries/${library.id}/roots`, { headers, data: { path: folder } })).ok()).toBe(true);
        const { runId } = await (await api.post(`api/libraries/${library.id}/scan`, { headers })).json() as { runId: number };
        await expect.poll(async () => (await (await api.get(`api/tasks/${runId}`, { headers })).json()).outcome, { timeout: 30_000 }).toBe('succeeded');
        await page.evaluate(id => localStorage.setItem('librarion.library', String(id)), library.id);
        await page.reload();

        // Authors, from the navigation.
        if (isPhoneWidth(page))
            await page.locator('.menu-button').tap();
        await press(page.locator('#main-nav').getByRole('link', { name: 'Authors' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Authors');
        await expect(page.locator('.browse-count')).toHaveText(`With books in ${name}`);

        // Go to: the list starts at the name typed, kept in the address.
        await page.getByRole('searchbox', { name: 'Go to' }).fill(author.split(' ')[1].toLowerCase());
        await expect(page).toHaveURL(/from=/);
        const row = page.locator('.value a').first();
        await expect(row.locator('.value-name')).toHaveText(author);
        await expect(row.locator('.value-count')).toHaveText('3');

        // The author's page: what it is, its count, its books.
        await press(row, isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(author);
        await expect(page.locator('.tag-kind')).toHaveText('Author');
        await expect(page.locator('.browse-count')).toHaveText(`3 books in ${name}`);
        await expect(page.getByRole('list', { name: `Books with ${author}` }).locator('.book')).toHaveCount(3);

        // Back: the list where it was.
        await page.goBack();
        await expect(page.locator('.value a').first().locator('.value-name')).toHaveText(author);

        // The series, by number.
        await page.goto('./fields/series');
        await page.getByRole('searchbox', { name: 'Go to' }).fill(series);
        // The jump first (300 ms after typing): pressing before it, the jump's own navigation came after and left the page.
        await expect(page).toHaveURL(/from=/);
        const saga = page.locator('.value a').first();
        await expect(saga.locator('.value-name')).toHaveText(series);
        await press(saga, isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(series);
        await expect(page.getByRole('combobox', { name: 'Sort' })).toHaveValue('number');
        await expect(page.locator('.book .book-title')).toHaveText(['Part One', 'Part Two', 'Part Three']);

        // All fields leads to every list.
        await page.goto('./fields');
        await expect(page.locator('.field-card').first()).toHaveText('Authors');
        await press(page.locator('.field-card', { hasText: 'Tags' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Tags');
    });
});
