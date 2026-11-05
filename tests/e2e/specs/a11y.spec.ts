// Accessibility of the main screens on a phone (as SFE): axe-core's WCAG 2.2 A and AA rules (fixtures/axe.ts). Once
// per run (android): the rules don't depend on the engine, and layouts are checked elsewhere. The setup page is
// audited by the first start (it shows only once).
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { scannedLibrary } from '../fixtures/library';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { signedOut } from '../paths';

test.describe('@a11y', () => {
    test.beforeEach(({}, testInfo) => test.skip(testInfo.project.name !== 'android', 'once per run, on the android device'));

    test('the home page (the library) in both themes, the navigation and the account menu open', async ({ page }) => {
        await page.goto('./');
        // Its books, or what to do when it has none.
        await expect(page.locator('lib-browse-page :is(.books, .empty)').first()).toBeVisible();
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

    test('browsing by value: the fields, a list of values and a value\'s page', async ({ page }) => {
        await page.goto('./fields');
        await expect(page.locator('.field-card').first()).toBeVisible();
        await audit(page, 'all fields');
        await page.goto('./fields/author');
        await expect(page.locator('lib-values-page :is(.values, .empty)').first()).toBeVisible();
        await audit(page, 'authors');
        // A value's page when the library has one (the run's other tests fill it).
        const first = page.locator('.value a').first();
        if (await first.count()) {
            await first.tap();
            await expect(page.locator('.tag-kind')).toBeVisible();
            await expect(page.locator('lib-book-list :is(.books, .empty)').first()).toBeVisible();
            await audit(page, 'an author');
        }
    });

    test('a book\'s page', async ({ page }, testInfo) => {
        // A book of its own, two files: one loose, one in a ZIP (the same work: a possible duplicate shows too).
        await scannedLibrary(page, uniqueName('A11y book', testInfo), folder => {
            fs.writeFileSync(path.join(folder, 'Plain Words.fb2'), fb2('Plain Words', 'Ada Reader'));
            writeZip(path.join(folder, 'more.zip'), [{ name: 'Plain Words.fb2', data: fb2('Plain Words', 'Ada Reader', { name: 'Clear Saga', number: 1 }) }]);
        });
        await page.locator('.book-link').first().tap();
        await expect(page.locator('.book-files')).toBeVisible();
        await expect(page.locator('.book-duplicate')).toBeVisible();
        await audit(page, 'a book');
        await page.getByRole('button', { name: 'Edit', exact: true }).tap();
        await expect(page.getByRole('dialog', { name: 'Edit the book' })).toBeVisible();
        await audit(page, 'the book editor');
    });

    test('search: the suggestions open, the results and the help, Refine open', async ({ page }, testInfo) => {
        await scannedLibrary(page, uniqueName('A11y search', testInfo), folder => {
            fs.writeFileSync(path.join(folder, 'Clear Words.fb2'), fb2('Clear Words', 'Ada Reader'));
        });
        const box = page.getByRole('combobox', { name: 'Search books' });
        await box.fill('reade');
        await expect(page.locator('#search-suggestions').getByRole('option').first()).toBeVisible();
        await audit(page, 'search suggestions');
        await box.press('Enter');
        await expect(page.locator('lib-search-page .book')).toHaveCount(1);
        await page.locator('.search-help summary').tap();
        await audit(page, 'search results');
        await page.getByRole('button', { name: 'Refine', exact: true }).tap();
        await expect(page.getByRole('dialog', { name: 'Refine' }).locator('.facet-value').first()).toBeVisible();
        await audit(page, 'search refine');
    });

    test.describe('light colors', () => {
        test.use({ colorScheme: 'light' });

        test('the main screens: the library, a book, search with Refine, the administration and a settings page', async ({ page }, testInfo) => {
            await scannedLibrary(page, uniqueName('A11y light', testInfo), folder => {
                fs.writeFileSync(path.join(folder, 'Light Words.fb2'), fb2('Light Words', 'Ada Reader'));
            });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await expect(page.locator('lib-browse-page .book')).toHaveCount(1);
            await audit(page, 'home (light)');
            await page.locator('.book-link').first().tap();
            await expect(page.locator('.book-files')).toBeVisible();
            await audit(page, 'a book (light)');

            const box = page.getByRole('combobox', { name: 'Search books' });
            await box.fill('light');
            await box.press('Enter');
            await expect(page.locator('lib-search-page .book')).toHaveCount(1);
            await audit(page, 'search results (light)');
            await page.getByRole('button', { name: 'Refine', exact: true }).tap();
            await expect(page.getByRole('dialog', { name: 'Refine' }).locator('.facet-value').first()).toBeVisible();
            await audit(page, 'search refine (light)');

            await page.goto('./admin');
            await expect(page.locator('.admin-nav a').first()).toBeVisible();
            await audit(page, 'administration (light)');
            await page.goto('./admin/settings/server');
            await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
            await audit(page, 'settings (light)');
            await page.goto('./admin/tags/tags');
            await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
            await page.getByRole('button', { name: 'New tag' }).tap();
            await expect(page.getByRole('dialog', { name: 'New tag' })).toBeVisible();
            await audit(page, 'tag editor (light)');
        });
    });

    test('the tag manager: the list, the editor and the merges', async ({ page }) => {
        await page.goto('./admin/tags/tags');
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
        await audit(page, 'tags');
        await page.getByRole('button', { name: 'New tag' }).tap();
        await expect(page.getByRole('dialog', { name: 'New tag' })).toBeVisible();
        await audit(page, 'tag editor');
        await page.keyboard.press('Escape');
        await page.goto('./admin/tags/merges');
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
        await audit(page, 'merges');
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
        // On this phone, the overview is the list of sections; a section with sub-pages shows them as tabs.
        await page.goto('./admin');
        await expect(page.locator('.admin-nav a').first()).toBeVisible();
        await audit(page, 'administration: list of sections');
        await page.goto('./admin/settings/server');
        await expect(page.getByRole('tab', { name: 'Whole server' })).toHaveAttribute('aria-selected', 'true');
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
        await audit(page, 'administration: section with tabs');
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
            await expect(page.locator('.admin-head h1')).toBeVisible();
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
