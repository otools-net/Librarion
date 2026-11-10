// The folder view on every device (stage 6, chunk 8b): a library whose folder holds author folders and a ZIP, scanned
// through the API; Folders in the navigation shows the library's only root as the top (its folders with their counts),
// a folder shows the folders above it as links and its own books, a ZIP opens like a folder. The phone layout is
// checked, and axe audits the view in both themes once per run.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { expectFitsScreen, expectTapTargets } from '../fixtures/layout';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';

test.describe('@browse', () => {
    test('the library is browsed by its folders and ZIPs, each with its books @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Folders', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        await scannedLibrary(page, name, folder => {
            fs.mkdirSync(path.join(folder, 'Authors', 'Quill'), { recursive: true });
            for (const title of ['Ink', 'Paper'])
                fs.writeFileSync(path.join(folder, 'Authors', 'Quill', `${title}.fb2`), fb2(title, `Ann Quill${suffix}`));
            fs.mkdirSync(path.join(folder, 'lib'));
            writeZip(path.join(folder, 'lib', 'fb2-0001.zip'), [{ name: 'Zipped.fb2', data: fb2('Zipped', `Zed Archive${suffix}`) }]);
        });

        if (isPhoneWidth(page))
            await page.locator('.menu-button').tap();
        await press(page.locator('#main-nav').getByRole('link', { name: 'Folders' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Folders');
        await expect(page.locator('.browse-count')).toHaveText('2 folders · 3 files');
        const rows = page.getByRole('list', { name: 'Folders' }).locator('.folder-row a');
        await expect(rows.locator('.value-name')).toHaveText(['Authors', 'lib']);
        await expect(rows.first().locator('.value-count')).toHaveText('2');
        if (isPhoneWidth(page)) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-folder-page');
        }

        // Down two folders: the path above, the folder's own books.
        await press(rows.first(), isMobile);
        await expect(page).toHaveURL(/\/folders\/\d+\/Authors$/);
        await press(page.locator('.folder-row a', { hasText: 'Quill' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Quill');
        await expect(page.locator('.folder-crumbs a')).toHaveText([name, 'Authors']);
        await expect(page.getByRole('list', { name: 'Books in Quill' }).locator('.book')).toHaveCount(2);
        if (isPhoneWidth(page))
            await expectFitsScreen(page);

        // A default tag of the folder, given to the books already there too (stage 6 chunk 8c).
        const tag = `Quilled ${suffix}`;
        await press(page.locator('.folder-defaults').getByRole('button', { name: 'Default tags' }), isMobile);
        const dialog = page.getByRole('dialog', { name: 'Default tags' });
        await dialog.getByRole('combobox', { name: 'Add a default tag' }).fill(tag);
        await press(dialog.getByRole('option', { name: `Create “${tag}”` }), isMobile);
        await press(dialog.getByRole('radio', { name: 'Also the 2 files already there' }), isMobile);
        if (isPhoneWidth(page))
            await expectFitsScreen(page);
        await press(dialog.getByRole('button', { name: 'Add', exact: true }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: `${tag} is a default tag now.` })).toBeVisible();
        await expect(dialog.locator('.default-tag-row .chip')).toHaveText(tag);
        if (testInfo.project.name === 'android')
            await audit(page, 'default tags');
        await press(dialog.getByRole('button', { name: 'Close', exact: true }).last(), isMobile);
        // Gone first: on phones a sheet closes through the history, which must be done before the next page opens.
        await expect(dialog).toHaveCount(0);
        await expect(page.locator('.folder-defaults .chip')).toHaveText(tag);
        await press(page.locator('.book', { hasText: 'Ink' }).locator('.book-link'), isMobile);
        await expect(page).toHaveURL(/\/books\/\d+/);
        await expect(page.locator('lib-book-page .chip', { hasText: tag })).toBeVisible();
        await page.goBack();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Quill');

        // Back to the top through the path, then into the ZIP.
        await press(page.locator('.folder-crumbs a', { hasText: name }), isMobile);
        await press(page.locator('.folder-row a', { hasText: 'lib' }), isMobile);
        const zip = page.locator('.folder-row a', { hasText: 'fb2-0001.zip' });
        await expect(zip.locator('use')).toHaveAttribute('href', '#icon-zip');
        await press(zip, isMobile);
        await expect(page.getByRole('list', { name: 'Books in fb2-0001.zip' }).locator('.book')).toHaveCount(1);

        if (testInfo.project.name === 'android') {
            await audit(page, 'a folder (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await page.reload();
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await expect(page.locator('lib-folder-page .book')).toHaveCount(1);
            await audit(page, 'a folder (light)');
        }
    });
});
