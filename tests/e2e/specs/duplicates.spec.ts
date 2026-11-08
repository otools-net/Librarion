// Administration › Catalog › Duplicates on every device (stage 6, chunk 12): a library with two pairs of the same work
// (each device's own author, so devices never share a group); one pair compared and merged (the group leaves, the kept
// book has both files), the other set apart as not duplicates after the question. The phone layout is checked, and
// axe audits the page and the compare sheet in both themes once per run.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { expectFitsScreen, expectTapTargets } from '../fixtures/layout';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';

test.describe('@tags', () => {
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('duplicates are compared and merged, or set apart @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Duplicates', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const author = `Ann Quill${suffix}`;
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'Dune.fb2'), fb2('Dune', author));
            fs.writeFileSync(path.join(folder, 'Dune (scan).fb2'), fb2('Dune', author, { name: 'Desert', number: 1 }));
            fs.writeFileSync(path.join(folder, 'Emma.fb2'), fb2('Emma', author));
            fs.writeFileSync(path.join(folder, 'Emma (other).fb2'), fb2('Emma', author, undefined, ['classic']));
        });

        // The library shown and its calls answered first: WebKit reports those a navigation cuts off as errors.
        await expect(page.locator('.book', { hasText: 'Emma' }).first()).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.goto('./admin/duplicates/groups');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Duplicates');
        const groups = page.getByRole('list', { name: 'Possible duplicates' }).locator('> li.duplicate-group');
        const group = (title: string) => groups.filter({ hasText: title });
        await expect(group('Dune')).toHaveCount(1);
        await expect(group('Emma')).toHaveCount(1);
        await expect(group('Dune')).toContainText('Same title and author');
        if (isMobile) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-duplicates-section');
        }
        if (testInfo.project.name === 'android')
            await audit(page, 'duplicates (dark)');

        // Compared and merged.
        await press(group('Dune').getByRole('button', { name: 'Compare and merge' }), isMobile);
        const sheet = page.getByRole('dialog', { name: 'Compare and merge' });
        await expect(sheet.locator('.compare-book')).toHaveCount(2);
        await expect(sheet.locator('.compare-book.kept')).toHaveCount(1);
        if (isMobile)
            await expectFitsScreen(page);
        if (testInfo.project.name === 'android') {
            await audit(page, 'the compare sheet (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'the compare sheet (light)');
        }
        await press(sheet.getByRole('button', { name: 'Merge 2 books' }), isMobile);
        await expect(sheet).toHaveCount(0);
        await expect(page.getByRole('status').filter({ hasText: 'Merged into “Dune”.' })).toBeVisible();
        await expect(group('Dune')).toHaveCount(0);
        if (testInfo.project.name === 'android')
            await audit(page, 'duplicates (light)');

        // Set apart, after the question.
        await press(group('Emma').getByRole('button', { name: 'Not duplicates' }), isMobile);
        const question = page.getByRole('dialog', { name: 'Not duplicates?' });
        await press(question.getByRole('button', { name: 'Not duplicates' }), isMobile);
        await expect(group('Emma')).toHaveCount(0);
        await expect(page.getByText('No possible duplicates in this library.')).toBeVisible();

        // The kept book holds both files.
        await page.goto('./');
        await press(page.locator('.book-link', { hasText: 'Dune' }), isMobile);
        await expect(page.locator('.book-file')).toHaveCount(2);
    });
});
