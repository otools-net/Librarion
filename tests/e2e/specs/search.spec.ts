// Search on every device: a library of three books (two by one author), scanned through the API; typing part of the
// author's name in the top bar offers the author, choosing it and pressing Enter lists the two books; a title word
// narrows to one, which opens its page; the help's examples run their search. Refine narrows by a value with its
// count, takes it out again, and leaves a value's books out.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';

test.describe('@search', () => {
    // scannedLibrary reloads the page while the progress panel's poll may be on its way: WebKit reports the fetch it
    // cuts off as an "access control" error.
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('the top bar finds books by words and suggests authors as you type @smoke @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Search', testInfo);
        const tail = name.slice(name.lastIndexOf('-') + 1).replace(/[^a-z]/gi, '');
        // Names of this test only: the tenant's vocabulary is shared by every library of the run.
        const author = `Odette Quillon${tail}`;
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'a.fb2'), fb2('Lanterns of Brass', author));
            fs.writeFileSync(path.join(folder, 'b.fb2'), fb2('Harbour of Lanterns', author));
            fs.writeFileSync(path.join(folder, 'c.fb2'), fb2('A Quiet Mill', 'Bram Other'));
        });

        const box = page.getByRole('combobox', { name: 'Search books' });
        await box.fill(`quillon${tail.slice(0, 3)}`);
        const option = page.getByRole('option', { name: new RegExp(author) });
        await expect(option).toBeVisible();
        await expect(option.locator('.suggestion-count')).toHaveText('2');
        await press(option, isMobile);
        await expect(box).toHaveValue(`author:"${author}" `);
        await box.press('Enter');

        await expect(page).toHaveURL(/\/search\?q=/);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Search');
        const results = page.getByRole('list', { name: /^Books matching/ }).locator('.book');
        await expect(results).toHaveCount(2);

        // A title word too: every term must match.
        await box.fill(`author:"${author}" harbour`);
        await box.press('Enter');
        await expect(results).toHaveCount(1);
        await press(results.first().locator('.book-link'), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Harbour of Lanterns');

        // Back to the results, then a word no book has.
        await page.goBack();
        await expect(results).toHaveCount(1);
        await box.fill('zzyzxnothing');
        await box.press('Enter');
        await expect(page.locator('lib-search-page .empty')).toContainText('No books match this search.');
    });

    test('Refine narrows a search by its values and leaves them out @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Facets', testInfo);
        const tail = name.slice(name.lastIndexOf('-') + 1).replace(/[^a-z]/gi, '');
        const author = `Mirelle Vantor${tail}`;
        const other = `Osric Pellam${tail}`;
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'a.fb2'), fb2('Lanterns of Brass', author));
            fs.writeFileSync(path.join(folder, 'b.fb2'), fb2('Harbour of Lanterns', author));
            fs.writeFileSync(path.join(folder, 'c.fb2'), fb2('Lanterns Below', other));
        });

        const box = page.getByRole('combobox', { name: 'Search books' });
        await box.fill('lanterns');
        await box.press('Enter');
        const results = page.getByRole('list', { name: /^Books matching/ }).locator('.book');
        await expect(results).toHaveCount(3);
        await expect(page.locator('.search-count')).toHaveText(' · 3 books');

        const refine = page.getByRole('button', { name: 'Refine', exact: true });
        const sheet = page.getByRole('dialog', { name: 'Refine' });
        await press(refine, isMobile);
        await expect(sheet).toBeVisible();
        const authors = sheet.getByRole('region', { name: 'Authors' });
        const value = authors.getByRole('link', { name: new RegExp(`^${author}`) });
        await expect(value.locator('.facet-count')).toHaveText('2');
        await press(value, isMobile);
        await expect(sheet).toBeHidden();
        await expect(results).toHaveCount(2);
        await expect(box).toHaveValue(`lanterns author:"${author}"`);

        // Chosen: marked, and choosing it again takes it out.
        await press(refine, isMobile);
        await expect(value).toHaveAttribute('aria-current', 'true');
        await press(value, isMobile);
        await expect(results).toHaveCount(3);

        await press(refine, isMobile);
        await press(authors.getByRole('link', { name: `Leave out ${author}` }), isMobile);
        await expect(results).toHaveCount(1);
        await expect(box).toHaveValue(`lanterns -author:"${author}"`);
    });
});
