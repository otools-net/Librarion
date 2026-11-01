// Administration on every device: the administrator makes an account and a group giving it one right, and the
// account, signed in, finds that right and nothing else (only the libraries section) and uses it. Deleting asks in the
// page's own dialog (a sheet from the bottom on phones) and says it is done in a toast.
import { signIn, uniqueName } from '../fixtures/accounts';
import { adminSection } from '../fixtures/admin';
import { press } from '../fixtures/press';
import { expect, isPhoneWidth, test } from '../fixtures/test';

test.describe('@admin', () => {
    test('an account given a right through a group can use it, and only it @desktop', async ({ page, isMobile }, testInfo) => {
        const userName = uniqueName('librarian', testInfo);
        const password = 'librarian-password-1';
        const groupName = uniqueName('Librarians', testInfo);
        const libraryName = uniqueName('Shelf', testInfo);

        // The overview: a card per section, by group (on a phone, the list of sections).
        await page.goto('./admin');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Administration');
        const accounts = isPhoneWidth(page) ? adminSection(page, 'Accounts') : page.locator('.admin-card').getByRole('link', { name: 'Accounts' });
        await press(accounts, isMobile);
        await expect(page).toHaveURL(/\/admin\/users$/);

        // The account, in the built-in Users group.
        await press(page.getByRole('button', { name: 'New account' }), isMobile);
        await expect(page.getByRole('heading', { name: 'New account' })).toBeVisible();
        await page.getByLabel('Name', { exact: true }).fill(userName);
        await page.getByLabel('Password', { exact: true }).fill(password);
        await press(page.getByRole('checkbox', { name: 'Users', exact: true }), isMobile);
        await press(page.getByRole('button', { name: 'Save' }), isMobile);
        await expect(page.getByRole('list', { name: 'Accounts' }).getByText(userName).first()).toBeVisible();

        // A group managing the libraries, with the account in it.
        // On a phone a section is a page of its own: back to the list of sections first.
        if (isPhoneWidth(page))
            await press(page.getByRole('button', { name: 'Back to the sections of the administration' }), isMobile);
        await press(adminSection(page, 'Groups'), isMobile);
        await press(page.getByRole('button', { name: 'New group' }), isMobile);
        await page.getByLabel('Name', { exact: true }).fill(groupName);
        await press(page.getByRole('checkbox', { name: /^Manage libraries/ }), isMobile);
        await press(page.getByRole('checkbox', { name: userName, exact: true }), isMobile);
        await press(page.getByRole('button', { name: 'Save' }), isMobile);
        await expect(page.getByRole('list', { name: 'Groups' }).getByText(groupName)).toBeVisible();

        // The account signs in here (this browser only: the administrator's saved sign-in stays good).
        await page.context().clearCookies();
        await page.goto('./admin');
        await signIn(page, isMobile, userName, password);

        await expect(page).toHaveURL(/\/admin$/);
        await expect(page.locator('.admin-nav .admin-nav-title')).toHaveText(['My account', 'Libraries']);
        await press(adminSection(page, 'Libraries'), isMobile);
        await expect(page).toHaveURL(/\/admin\/libraries$/);

        await page.getByLabel('Name of a new library').fill(libraryName);
        await press(page.getByRole('button', { name: 'Add' }), isMobile);
        await expect(page.getByText(libraryName, { exact: true })).toBeVisible();
    });

    test('deleting a library asks in a dialog, a sheet on phones, and a toast says it is done @desktop', async ({ page, isMobile }, testInfo) => {
        const libraryName = uniqueName('Doomed', testInfo);
        await page.goto('./admin/libraries');
        await page.getByLabel('Name of a new library').fill(libraryName);
        await press(page.getByRole('button', { name: 'Add', exact: true }), isMobile);
        const row = page.getByRole('list', { name: 'Libraries' }).getByRole('listitem').filter({ hasText: libraryName });
        await expect(row).toBeVisible();

        await press(row.getByRole('button', { name: 'Delete' }), isMobile);
        const dialog = page.getByRole('dialog', { name: 'Delete the library?' });
        await expect(dialog).toBeVisible();
        await expect(dialog).toContainText(libraryName);
        // Cancel has the focus: Enter doesn't destroy anything.
        await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
        // Measured once it has risen (it slides up on phones).
        const viewport = page.viewportSize()!;
        const bottom = async () => { const box = (await dialog.boundingBox())!; return Math.round(box.y + box.height); };
        if (viewport.width <= 640) {
            await expect.poll(bottom).toBe(viewport.height);
            expect(Math.round((await dialog.boundingBox())!.width)).toBe(viewport.width);
        } else {
            expect(await bottom()).toBeLessThan(viewport.height);
        }
        await press(dialog.getByRole('button', { name: 'Cancel' }), isMobile);
        await expect(dialog).toBeHidden();
        await expect(row).toBeVisible();

        await press(row.getByRole('button', { name: 'Delete' }), isMobile);
        await press(page.getByRole('dialog').getByRole('button', { name: 'Delete' }), isMobile);
        await expect(row).toBeHidden();
        await expect(page.getByRole('status').filter({ hasText: `Library ${libraryName} deleted.` })).toBeVisible();
    });

    test('a settings page counts its unsaved changes and asks before they are lost @desktop', async ({ page, isMobile }) => {
        await page.goto('./admin/settings/tenant');
        const pageSize = page.getByLabel('Books per page');
        const before = await pageSize.inputValue();
        await pageSize.fill(String(Number(before) + 10));
        const bar = page.getByRole('region', { name: 'Unsaved changes' });
        await expect(bar.getByRole('status')).toHaveText('1 unsaved changes');

        // Leaving: the administration's column on wider screens, the back arrow on a phone.
        const leave = () => isPhoneWidth(page)
            ? press(page.getByRole('button', { name: 'Back to the sections of the administration' }), isMobile)
            : press(adminSection(page, 'Accounts'), isMobile);
        await leave();
        const dialog = page.getByRole('dialog', { name: 'Leave without saving?' });
        await expect(dialog).toBeVisible();
        await press(dialog.getByRole('button', { name: 'Cancel' }), isMobile);
        await expect(page).toHaveURL(/\/admin\/settings\/tenant$/);
        await expect(pageSize).toHaveValue(String(Number(before) + 10));

        await leave();
        await press(page.getByRole('dialog', { name: 'Leave without saving?' }).getByRole('button', { name: 'Leave without saving' }), isMobile);
        await expect(page).not.toHaveURL(/\/admin\/settings\/tenant$/);
        await page.goto('./admin/settings/tenant');
        await expect(page.getByLabel('Books per page')).toHaveValue(before);
    });

    test('the search finds a setting and opens it on its page, focused @desktop', async ({ page, isMobile }) => {
        // On a phone the box is above the list of sections; on wider screens above the column.
        await page.goto('./admin');
        const box = page.getByRole('searchbox', { name: 'Find a setting or a page' });
        await box.fill('books per page');
        const results = page.getByRole('list', { name: 'Found' });
        await expect(page.locator('.admin-search-count')).toHaveText(/^\d+ found$/);
        // Each word marked, the titles holding them all first.
        await expect(results.locator('.admin-search-title').first()).toHaveText('Books per page');
        await expect(results.locator('.admin-search-title').first().locator('mark')).toHaveText(['Books', 'per', 'page']);

        await press(results.getByRole('link').filter({ hasText: 'Settings › Whole server › Interface' }), isMobile);
        await expect(page).toHaveURL(/\/admin\/settings\/server$/);
        await expect(page.getByLabel('Books per page')).toBeFocused();

        // Esc clears the box and the sections come back (wider screens: the column stays beside the page).
        if (!isPhoneWidth(page)) {
            await expect(box).toHaveValue('books per page');
            await box.press('Escape');
            await expect(box).toHaveValue('');
            await expect(adminSection(page, 'Accounts')).toBeVisible();
        }
    });
});
