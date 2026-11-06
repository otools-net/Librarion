// The Imported tags page on every device (stage 6, chunk 9): a library whose books bring plain tags as FB2 keywords;
// Administration › Catalog › Imported tags lists them by books, one is kept (it leaves the tags to review), another is
// ignored after the question (its books lose it) and the ignore undone under Ignored (it is back to review). The phone
// layout is checked, and axe audits the page in both themes once per run. Names are the test's own: the tenant's
// mappings are shared by the devices' libraries.
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

    test('imported tags are kept, ignored and the ignore undone @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Imported', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const [kept, junk] = [`Keeper ${suffix}`, `Junk ${suffix}`];
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'One.fb2'), fb2('One', `Ann Quill${suffix}`, undefined, [kept, junk]));
            fs.writeFileSync(path.join(folder, 'Two.fb2'), fb2('Two', `Ann Quill${suffix}`, undefined, [kept]));
        });

        // The library shown and its calls answered first: WebKit reports those a navigation cuts off as errors.
        await expect(page.locator('.book', { hasText: 'Two' })).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.goto('./admin/importedTags/names');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Imported tags');
        const list = page.getByRole('list', { name: 'Imported tag list' });
        const row = (tag: string) => list.getByRole('listitem').filter({ hasText: tag });
        await expect(row(kept)).toContainText('2 books · from FB2');
        await expect(row(junk)).toContainText('1 books · from FB2');
        await expect(page.locator('.imported-summary')).toContainText('imported links in this library');
        if (isMobile) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-imported-tags-section');
        }

        // Kept: right as it is, no longer to review.
        await press(row(kept).getByRole('checkbox', { name: `Choose ${kept}` }), isMobile);
        const bar = page.getByRole('region', { name: 'Selected tags' });
        await expect(bar).toContainText('1 selected');
        if (testInfo.project.name === 'android')
            await audit(page, 'imported tags (dark)');
        await press(bar.getByRole('button', { name: 'Keep' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: '1 tags reviewed.' })).toBeVisible();
        await expect(row(kept)).toHaveCount(0);

        // Ignored after the question: its book loses it.
        await press(row(junk).getByRole('checkbox', { name: `Choose ${junk}` }), isMobile);
        await press(bar.getByRole('button', { name: 'Ignore' }), isMobile);
        const question = page.getByRole('dialog', { name: 'Ignore the chosen tags (1)?' });
        await expect(question).toContainText('1 books here');
        await press(question.getByRole('button', { name: 'Ignore' }), isMobile);
        // Done at once, or a run while another of the tenant goes (the other devices' scans): the list follows it.
        await expect(row(junk)).toHaveCount(0);

        // Ignored: undone after a question, the tag to review again.
        await page.goto('./admin/importedTags/ignored');
        const ignored = page.getByRole('list', { name: 'Ignored' }).getByRole('listitem').filter({ hasText: junk });
        await expect(ignored).toContainText('1 links taken off');
        await press(ignored.getByRole('button', { name: 'Undo' }), isMobile);
        await press(page.getByRole('dialog', { name: 'Undo the ignore?' }).getByRole('button', { name: 'Undo' }), isMobile);
        await expect(ignored).toHaveCount(0);
        await page.goto('./admin/importedTags/names');
        await expect(row(junk)).toContainText('1 books');

        if (testInfo.project.name === 'android') {
            await page.emulateMedia({ colorScheme: 'light' });
            await page.reload();
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await expect(row(junk)).toBeVisible();
            await audit(page, 'imported tags (light)');
        }
        await page.evaluate(() => scrollTo({ top: 0 }));
    });
});
