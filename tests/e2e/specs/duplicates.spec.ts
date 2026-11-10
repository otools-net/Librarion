// Administration › Catalog › Duplicates on every device (stage 6, chunks 12 and 13): a library with two pairs of the
// same work and a pair with the same ISBN (each device's own author and ISBN, so devices never share a group); one pair
// compared and merged (the group leaves, the kept book has both files), another set apart as not duplicates after the
// question, the ISBN pair chosen and merged in bulk as a run; under Merges the first merge undone; the bulk-merged book
// split again from its page. The phone layout is checked, and axe audits the page, the compare sheet and Merges in
// both themes once per run.
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
        // 13 digits of the device's own (the reader checks the shape only).
        const isbn = `978${[...suffix].reduce((n, c) => (n * 31 + c.charCodeAt(0)) % 1e10, 7).toString().padStart(10, '0')}`;
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'Dune.fb2'), fb2('Dune', author));
            fs.writeFileSync(path.join(folder, 'Dune (scan).fb2'), fb2('Dune', author, { name: 'Desert', number: 1 }));
            fs.writeFileSync(path.join(folder, 'Emma.fb2'), fb2('Emma', author));
            fs.writeFileSync(path.join(folder, 'Emma (other).fb2'), fb2('Emma', author, undefined, ['classic']));
            fs.writeFileSync(path.join(folder, 'Messiah.fb2'), fb2('Messiah', author, undefined, undefined, isbn));
            fs.writeFileSync(path.join(folder, 'Dune Messiah.fb2'), fb2('Dune Messiah', author, undefined, undefined, isbn));
        });

        // The library shown and its calls answered first: WebKit reports those a navigation cuts off as errors.
        await expect(page.locator('.book', { hasText: 'Emma' }).first()).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.goto('./admin/duplicates/groups');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Duplicates');
        const groups = page.getByRole('list', { name: 'Possible duplicates' }).locator('> li.duplicate-group');
        const group = (title: string) => groups.filter({ has: page.locator('.book-title', { hasText: new RegExp(`^${title}$`) }) });
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

        // The same ISBN: chosen and merged in bulk, as a run; the list is read again when it ends.
        const messiah = group('Messiah');
        await expect(messiah).toContainText('Same ISBN');
        await press(messiah.getByRole('checkbox', { name: 'Same ISBN' }), isMobile);
        await press(page.getByRole('button', { name: 'Merge 1 chosen' }), isMobile);
        await press(page.getByRole('dialog', { name: 'Merge these groups?' }).getByRole('button', { name: 'Merge' }), isMobile);
        await expect(messiah).toHaveCount(0);
        await expect(page.getByText('No possible duplicates in this library.')).toBeVisible();

        // The kept book holds both files.
        await page.goto('./');
        await press(page.locator('.book-link', { has: page.locator('.book-title', { hasText: /^Dune$/ }) }), isMobile);
        await expect(page.locator('.book-file')).toHaveCount(2);

        // Merges: the compare's merge undone whole.
        await page.goto('./admin/duplicates/merges');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Merges');
        const merges = page.getByRole('list', { name: 'Book merges' }).locator('> li');
        await expect(merges).toHaveCount(2);
        const dune = merges.filter({ has: page.locator('.row-title', { hasText: /^Dune → Dune$/ }) });
        if (isMobile) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-duplicates-section');
        }
        if (testInfo.project.name === 'android') {
            await audit(page, 'merges (light)');
            await page.emulateMedia({ colorScheme: 'dark' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
            await audit(page, 'merges (dark)');
        }
        await press(dune.getByRole('button', { name: 'Undo' }), isMobile);
        await press(page.getByRole('dialog', { name: 'Undo the merge?' }).getByRole('button', { name: 'Undo' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: 'The book is back.' })).toBeVisible();
        await expect(dune.locator('.badge')).toHaveText('Undone');

        // Split: the bulk-merged book's second file a book of its own again.
        await page.goto('./');
        await press(page.locator('.book-link', { hasText: 'Messiah' }).first(), isMobile);
        await expect(page.locator('.book-file')).toHaveCount(2);
        await press(page.getByRole('button', { name: 'Split…' }), isMobile);
        if (isMobile)
            await expectFitsScreen(page);
        await press(page.locator('.book-file-check').first(), isMobile);
        await press(page.getByRole('button', { name: 'Split 1 file off' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: 'Split off as' })).toBeVisible();
        await expect(page.locator('.book-file')).toHaveCount(1);
    });
});
