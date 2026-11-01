// Accessibility of the main screens on a phone (as SFE): axe-core's WCAG 2.2 A and AA rules. Serious and critical
// findings fail; moderate and minor ones are listed in the report. Once per run (android): the rules don't depend on
// the engine, and layouts are checked elsewhere.
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from '../fixtures/test';

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function audit(page: Page, screen: string): Promise<void> {
    const { violations } = await new AxeBuilder({ page }).withTags(TAGS).analyze();
    const line = (v: (typeof violations)[number]) =>
        `${v.id} (${v.impact}): ${v.help} — ${v.nodes.slice(0, 4).map(node => node.target.join(' ')).join(' | ')}`;
    const minor = violations.filter(v => v.impact !== 'serious' && v.impact !== 'critical');
    if (minor.length)
        test.info().annotations.push({ type: `a11y ${screen}`, description: minor.map(line).join('\n') });
    expect(violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(line), `${screen}: serious or critical`).toEqual([]);
}

test.describe('@a11y', () => {
    test.beforeEach(({}, testInfo) => test.skip(testInfo.project.name !== 'android', 'once per run, on the android device'));

    test('the home page in both themes, the drawer open', async ({ page }) => {
        await page.goto('./');
        await expect(page.locator('.facts')).toBeVisible();
        await audit(page, 'home (dark)');

        await page.getByRole('button', { name: 'Switch to light colors' }).tap();
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
        await audit(page, 'home (light)');

        await page.locator('.menu-button').tap();
        await expect(page.locator('#main-nav')).toBeVisible();
        await audit(page, 'drawer');
        await page.keyboard.press('Escape');
        await expect(page.locator('#main-nav')).toBeHidden();
    });

    test('the page not found', async ({ page }) => {
        await page.goto('./missing');
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
        await audit(page, 'not found');
    });
});
