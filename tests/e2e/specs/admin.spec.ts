// Administration on every device: the administrator makes an account and a group giving it one right, and the
// account, signed in, finds that right and nothing else (only the libraries section) and uses it.
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
});
