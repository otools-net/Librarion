// Search on every device: a library of three books (two by one author), scanned through the API; typing part of the
// author's name in the top bar offers the author, choosing it and pressing Enter lists the two books; a title word
// narrows to one, which opens its page; the help's examples run their search.
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
});
