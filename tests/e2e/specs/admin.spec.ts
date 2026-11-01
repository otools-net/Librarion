// Administration on every device: the administrator makes an account and a group giving it one right, and the
// account, signed in, finds that right and nothing else (only the libraries section) and uses it. Deleting asks in the
// page's own dialog (a sheet from the bottom on phones) and says it is done in a toast.
import { signIn, uniqueName } from '../fixtures/accounts';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';

test.describe('@admin', () => {
    test('an account given a right through a group can use it, and only it @desktop', async ({ page, isMobile }, testInfo) => {
        const userName = uniqueName('librarian', testInfo);
        const password = 'librarian-password-1';
        const groupName = uniqueName('Librarians', testInfo);
        const libraryName = uniqueName('Shelf', testInfo);

        await page.goto('./admin');
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
        await press(page.getByRole('navigation', { name: 'Sections of the administration' }).getByRole('link', { name: 'Groups' }), isMobile);
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

        await expect(page).toHaveURL(/\/admin\/libraries$/);
        const sections = page.getByRole('navigation', { name: 'Sections of the administration' }).getByRole('link');
        await expect(sections).toHaveText(['Libraries']);

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
});
