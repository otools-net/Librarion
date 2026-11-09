// Administration › Catalog › Tags › Possible duplicates on every device (stage 6, chunk 14): a library whose books are by
// "Stephen" and "Steven" of the device's own surname (devices never share a group, the vocabulary being the tenant's);
// the two authors shown as sounding alike, Stephen kept, Steven merged into it after the question, the group gone and
// the merged author's book under Stephen. The phone layout is checked, and axe audits the page in both themes once per run.
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

    test('authors that sound alike are merged from Possible duplicates @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('TagDuplicates', testInfo);
        const surname = `Quill${name.slice(name.lastIndexOf('-') + 1)}`;
        await scannedLibrary(page, name, folder => {
            fs.writeFileSync(path.join(folder, 'Carrie.fb2'), fb2('Carrie', `Stephen ${surname}`));
            fs.writeFileSync(path.join(folder, 'It.fb2'), fb2('It', `Stephen ${surname}`));
            fs.writeFileSync(path.join(folder, 'Misery.fb2'), fb2('Misery', `Steven ${surname}`));
        });

        // The library shown and its calls answered first: WebKit reports those a navigation cuts off as errors.
        await expect(page.locator('.book', { hasText: 'Misery' }).first()).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.goto('./admin/tags/duplicates');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Possible duplicates');
        const group = page.getByRole('list', { name: 'Possible duplicate tags' }).locator('> li.duplicate-group')
            .filter({ hasText: `Steven ${surname}` });
        await expect(group).toHaveCount(1);
        await expect(group).toContainText('Sound alike');
        await expect(group.getByRole('radio', { name: new RegExp(`^Stephen ${surname}`) })).toBeChecked();
        if (isMobile) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-tag-duplicates-section');
        }
        if (testInfo.project.name === 'android') {
            await audit(page, 'possible duplicate tags (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'possible duplicate tags (light)');
        }

        await press(group.getByRole('button', { name: `Merge into “Stephen ${surname}”` }), isMobile);
        await press(page.getByRole('dialog', { name: 'Merge these tags?' }).getByRole('button', { name: 'Merge' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: `Merged into “Stephen ${surname}”.` })).toBeVisible();
        await expect(group).toHaveCount(0);

        // Misery is Stephen's now.
        await page.goto('./');
        await press(page.locator('.book-link', { hasText: 'Misery' }), isMobile);
        await expect(page.locator('.book-head').getByRole('link', { name: `Stephen ${surname}` })).toBeVisible();
    });
});
