// Administration › Catalog › Cover styles on every device (stage 6, chunk 10b): every drawn template with its samples
// and switch; the layout is checked on phones, and axe audits the page in both themes once per run. The tenant's styles
// are shared by the devices running at once, so only the desktop changes them: Geometric turned off, its books given
// another style by a run the panel follows, then turned on again (covers.spec only uses Minimal).
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

    test('cover styles show every template; one is turned off, its books given another, and on again @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Styles', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        await scannedLibrary(page, name, folder => fs.writeFileSync(path.join(folder, 'Styled.fb2'), fb2('Styled', `Ann Quill${suffix}`)));

        // The library shown and its calls answered first: WebKit reports those a navigation cuts off as errors.
        await expect(page.locator('.book', { hasText: 'Styled' })).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.goto('./admin/coverStyles/templates');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Cover styles');
        const templates = page.getByRole('list', { name: 'Drawn cover styles' }).getByRole('listitem');
        await expect(templates).toHaveCount(5);
        const first = templates.first().locator('img').first();
        await expect.poll(() => first.evaluate(img => (img as HTMLImageElement).naturalWidth), { message: 'a sample is drawn' }).toBe(240);
        const geometric = templates.filter({ has: page.getByRole('heading', { name: 'Geometric' }) });
        await expect(geometric.locator('img')).toHaveCount(8);
        if (isMobile) {
            await expectFitsScreen(page);
            await expectTapTargets(page, 'lib-cover-styles-section');
        }
        if (testInfo.project.name === 'android') {
            await audit(page, 'cover styles (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'cover styles (light)');
        }
        if (testInfo.project.name !== 'desktop')
            return;

        const toggle = geometric.getByRole('switch', { name: 'Geometric for new books' });
        await expect(toggle).toBeChecked();
        await press(toggle, isMobile);
        await expect(toggle).not.toBeChecked();
        await press(geometric.getByRole('button', { name: 'Give its books another style' }), isMobile);
        const question = page.getByRole('dialog', { name: 'Give the books of Geometric another style?' });
        await expect(question).toContainText('A style chosen on a book\'s page stays.');
        await press(question.getByRole('button', { name: 'Give another style' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: 'Giving the books of Geometric another style…' })).toBeVisible();
        await expect(page.locator('lib-task-panel')).toContainText('Cover styles given again', { timeout: 30_000 });

        await press(toggle, isMobile);
        await expect(toggle).toBeChecked();
        await expect(geometric.getByRole('button', { name: 'Give its books another style' })).toHaveCount(0);
    });
});
