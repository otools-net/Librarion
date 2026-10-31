// The app's frame on every device: it opens, its navigation works by finger, the theme sticks, unknown pages say so.
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { press } from '../fixtures/press';

test.describe('@smoke', () => {
    test('the home page shows what runs @desktop', async ({ page }) => {
        await page.goto('./');

        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome to Librarion');
        await expect(page.locator('.facts .version')).toHaveText(/^\d+\.\d+\.\d+/);
        await expect(page.locator('.facts .database')).toHaveText('SQLite');
    });

    test('on a phone the navigation is a drawer that the backdrop closes', async ({ page }) => {
        test.skip(!isPhoneWidth(page), 'wider screens show the sidebar');
        await page.goto('./');
        const sidebar = page.locator('#main-nav');
        await expect(sidebar).toBeHidden();

        await page.locator('.menu-button').tap();
        await expect(sidebar).toBeVisible();
        await expect(page.locator('.menu-button')).toHaveAttribute('aria-expanded', 'true');
        // The links are reachable by finger while it is open (the final layout check runs on the closed state).
        const home = sidebar.getByRole('link', { name: 'Home' });
        await expect(home).toBeInViewport();

        // A tap beside the drawer, on the dimmed page.
        const width = page.viewportSize()!.width;
        await page.locator('.drawer-backdrop').tap({ position: { x: width - 20, y: 200 } });
        await expect(sidebar).toBeHidden();
    });

    test('on wider screens the sidebar is always there, with no menu button @desktop', async ({ page }) => {
        test.skip(isPhoneWidth(page), 'phones use the drawer');
        await page.goto('./');

        await expect(page.locator('#main-nav')).toBeVisible();
        await expect(page.locator('.menu-button')).toBeHidden();
    });

    test('the theme switch changes the colors and survives a reload @desktop', async ({ page, isMobile }) => {
        await page.goto('./');
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

        await press(page.getByRole('button', { name: 'Switch to light colors' }), isMobile);
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

        await page.reload();
        await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
        await expect(page.getByRole('button', { name: 'Switch to dark colors' })).toBeVisible();
    });

    test('an unknown address says so and leads back home @desktop', async ({ page, isMobile }) => {
        await page.goto('./no/such/page');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Page not found');

        await press(page.getByRole('link', { name: 'Back to the home page' }), isMobile);

        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome to Librarion');
    });
});
