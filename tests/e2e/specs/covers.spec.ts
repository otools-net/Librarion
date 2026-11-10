// A book's cover chosen on its page, on every device (stage 6, chunk 10): a drawn style picked from the previews, a
// picture uploaded (a PNG written by the test), then back to the book's own cover; the page's cover follows each
// change. The phone layout is checked, and axe audits the sheet in both themes once per run.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { expectFitsScreen } from '../fixtures/layout';
import { scannedLibrary } from '../fixtures/library';
import { png } from '../fixtures/picture';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';

test.describe('@browse', () => {
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('a cover is drawn in another style, uploaded, then the book\'s own again @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Covers', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        await scannedLibrary(page, name, folder => fs.writeFileSync(path.join(folder, 'Dune.fb2'), fb2('Dune', `Ann Quill${suffix}`)));

        await press(page.locator('.book-link', { hasText: 'Dune' }), isMobile);
        await expect(page).toHaveURL(/\/books\/\d+/);
        const cover = page.locator('.book-head .book-page-cover img');
        const before = await cover.getAttribute('src');
        await press(page.getByRole('button', { name: 'Cover', exact: true }), isMobile);
        const sheet = page.getByRole('dialog', { name: 'Cover' });
        await expect(sheet).toContainText('The book\'s own cover');

        // A drawn style: its preview pressed, the page's cover changes.
        const minimal = sheet.getByRole('group', { name: 'Minimal' });
        await expect(minimal.getByRole('button')).toHaveCount(8);
        const style = minimal.getByRole('button', { name: 'Minimal, colors 3' });
        await style.scrollIntoViewIfNeeded();
        await press(style, isMobile);
        await expect(style).toHaveAttribute('aria-pressed', 'true');
        await expect(sheet).toContainText('A drawn cover.');
        await expect(cover).not.toHaveAttribute('src', before!);
        if (isMobile)
            await expectFitsScreen(page);
        if (testInfo.project.name === 'android')
            await audit(page, 'the cover sheet (dark)');

        // A picture uploaded.
        await sheet.locator('.cover-upload input').setInputFiles({ name: 'cover.png', mimeType: 'image/png', buffer: png(300, 450, [200, 60, 30]) });
        await expect(sheet).toContainText('An uploaded picture.');
        await expect(style).toHaveAttribute('aria-pressed', 'false');

        // Back to the book's own.
        await press(sheet.getByRole('button', { name: 'Use the book\'s cover' }), isMobile);
        await expect(sheet).toContainText('The book\'s own cover');
        await expect(sheet.getByRole('button', { name: 'Use the book\'s cover' })).toHaveCount(0);

        if (testInfo.project.name === 'android') {
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'the cover sheet (light)');
        }
        await press(sheet.getByRole('button', { name: 'Close', exact: true }).last(), isMobile);
        await expect(sheet).toHaveCount(0);
    });
});
