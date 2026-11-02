// The app's frame on every device: it opens, its navigation works by finger, the theme sticks, unknown pages say so.
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { adminSection } from '../fixtures/admin';
import { press } from '../fixtures/press';

test.describe('@smoke', () => {
    test('the home page is the library, and System says what runs @desktop', async ({ page }) => {
        await page.goto('./');
        await expect(page.locator('lib-browse-page')).toBeVisible();
        await expect(page.getByRole('heading', { level: 1 })).not.toBeEmpty();

        await page.goto('./admin/system');
        await expect(page.locator('.facts .version')).toHaveText(/^\d+\.\d+\.\d+/);
        await expect(page.locator('.facts .database')).toHaveText('SQLite');
    });

    test('on a phone the navigation is a sheet from the bottom that the backdrop closes', async ({ page }) => {
        test.skip(!isPhoneWidth(page), 'wider screens show the sidebar');
        await page.goto('./');
        const sidebar = page.locator('#main-nav');
        await expect(sidebar).toBeHidden();

        await page.locator('.menu-button').tap();
        await expect(sidebar).toBeVisible();
        await expect(page.locator('.menu-button')).toHaveAttribute('aria-expanded', 'true');
        // The links are reachable by finger while it is open (the final layout check runs on the closed state).
        const library = sidebar.getByRole('link', { name: 'Library' });
        await expect(library).toBeInViewport();
        // It rises from the bottom, across the whole width (measured once it stands still).
        const viewport = page.viewportSize()!;
        await expect.poll(async () => { const box = (await sidebar.boundingBox())!; return Math.round(box.y + box.height); }).toBe(viewport.height);

        // A tap above the sheet, on the dimmed page.
        await page.locator('.shell-backdrop').tap({ position: { x: viewport.width / 2, y: 80 } });
        await expect(sidebar).toBeHidden();
    });

    test('on wider screens the sidebar is always there, with no menu button @desktop', async ({ page }) => {
        test.skip(isPhoneWidth(page), 'phones use the sheet');
        await page.goto('./');

        await expect(page.locator('#main-nav')).toBeVisible();
        await expect(page.locator('.menu-button')).toBeHidden();
    });

    test('the theme switch in the account menu changes the colors and survives a reload @desktop', async ({ page, isMobile }) => {
        await page.goto('./');
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

        await press(page.locator('.account-button'), isMobile);
        await press(page.getByRole('button', { name: 'Switch to light colors' }), isMobile);
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

        await page.reload();
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
        await press(page.locator('.account-button'), isMobile);
        await expect(page.getByRole('button', { name: 'Switch to dark colors' })).toBeVisible();
    });

    test('an unknown address says so and leads back home @desktop', async ({ page, isMobile }) => {
        await page.goto('./no/such/page');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Page not found');

        await press(page.getByRole('link', { name: 'Back to the home page' }), isMobile);

        await expect(page.locator('lib-browse-page')).toBeVisible();
    });

    test('the administration is a page of its own, with a way back to the library @desktop', async ({ page, isMobile }) => {
        await page.goto('./');
        if (isPhoneWidth(page))
            await page.locator('.menu-button').tap();
        await press(page.locator('#main-nav').getByRole('link', { name: 'Administration' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Administration');
        await expect(page.locator('#main-nav')).toHaveCount(0);
        // Its own column of sections (on a phone, the list of sections), and breadcrumbs once a section is open.
        if (isPhoneWidth(page)) {
            await press(adminSection(page, 'Settings'), isMobile);
            await press(page.getByRole('tab', { name: 'Whole server' }), isMobile);
        } else {
            await press(page.locator('.admin-card').getByRole('link', { name: 'Whole server' }), isMobile);
            await expect(page.getByRole('navigation', { name: 'Where this page is' })).toContainText('Tenant and server');
        }
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Whole server');
        await expect(page.getByRole('navigation', { name: 'Where this page is' })).toContainText('Settings');
        if (isPhoneWidth(page)) {
            // Back to the list, then out to the library.
            await press(page.getByRole('button', { name: 'Back to the sections of the administration' }), isMobile);
            await expect(page.getByRole('heading', { level: 1 })).toHaveText('Administration');
        }
        // The library's search is not here (the administration has its own, for settings).
        await expect(page.getByRole('searchbox', { name: 'Search books' })).toHaveCount(0);
        await expect(page.getByRole('searchbox', { name: 'Find a setting or a page' })).toBeVisible();

        await press(page.getByRole('button', { name: 'Back to the library' }), isMobile);
        await expect(page.locator('lib-browse-page')).toBeVisible();
    });
});
