// Accessibility of the main screens on a phone (as SFE): axe-core's WCAG 2.2 A and AA rules (fixtures/axe.ts). Once
// per run (android): the rules don't depend on the engine, and layouts are checked elsewhere. The setup page is
// audited by the first start (it shows only once).
import { audit } from '../fixtures/axe';
import { expect, test } from '../fixtures/test';
import { signedOut } from '../paths';

test.describe('@a11y', () => {
    test.beforeEach(({}, testInfo) => test.skip(testInfo.project.name !== 'android', 'once per run, on the android device'));

    test('the home page in both themes, the navigation and the account menu open', async ({ page }) => {
        await page.goto('./');
        await expect(page.locator('.facts')).toBeVisible();
        await audit(page, 'home (dark)');

        await page.locator('.account-button').tap();
        await expect(page.locator('#account-menu')).toBeVisible();
        await audit(page, 'account menu');
        await page.getByRole('button', { name: 'Switch to light colors' }).tap();
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
        await page.keyboard.press('Escape');
        await expect(page.locator('#account-menu')).toHaveCount(0);
        await audit(page, 'home (light)');

        await page.locator('.menu-button').tap();
        await expect(page.locator('#main-nav')).toBeVisible();
        await audit(page, 'navigation sheet');
        await page.keyboard.press('Escape');
        await expect(page.locator('#main-nav')).toBeHidden();
    });

    test('the page not found', async ({ page }) => {
        await page.goto('./missing');
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
        await audit(page, 'not found');
    });

    test('my account', async ({ page }) => {
        await page.goto('./admin/account');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Preferences');
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
        await audit(page, 'account');
        await page.goto('./admin');
        await expect(page.locator('.admin-card').first()).toBeVisible();
        await audit(page, 'administration overview');
    });

    test('the administration with its editors open', async ({ page }) => {
        await page.goto('./admin/users');
        await page.getByRole('button', { name: 'New account' }).tap();
        await expect(page.getByRole('heading', { name: 'New account' })).toBeVisible();
        await audit(page, 'accounts');

        await page.goto('./admin/groups');
        await page.getByRole('button', { name: 'New group' }).tap();
        await expect(page.getByRole('heading', { name: 'New group' })).toBeVisible();
        await audit(page, 'groups');

        await page.goto('./admin/tenants');
        await page.getByRole('button', { name: 'Edit' }).first().tap();
        await expect(page.getByRole('heading', { name: 'Tenant', exact: true })).toBeVisible();
        await audit(page, 'tenants');

        for (const section of ['libraries', 'settings', 'librarySettings', 'security', 'system']) {
            await page.goto(`./admin/${section}`);
            await expect(page.getByRole('navigation', { name: 'Sections of the administration' }).getByRole('link', { name: /./ }).first()).toBeVisible();
            await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
            await audit(page, section);
        }
    });

    test.describe('signed out', () => {
        test.use({ storageState: signedOut });

        test('the sign-in page and the recovery', async ({ page }) => {
            await page.goto('./login');
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');
            await audit(page, 'sign-in');

            await page.getByRole('link', { name: 'Recover the account' }).tap();
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Recover the account');
            await audit(page, 'recovery');
        });
    });
});
