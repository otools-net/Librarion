// Signing in and out on every device: a visitor is sent to the sign-in page and back where it was, a wrong password
// is refused, signing out ends the session, and a server administrator recovers the account with the code the
// server shows where it runs. Each test makes its own account (fixtures/accounts.ts).
import fs from 'node:fs';
import path from 'node:path';
import { makeAccount, signIn } from '../fixtures/accounts';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { data, signedOut } from '../paths';

test.describe('@accounts', () => {
    // Signed out; the refused sign-ins answer 400, which the browser logs.
    test.use({ storageState: signedOut, allowedErrors: /status of 400/ });

    test('a visitor signs in and comes back where it was @desktop', async ({ page, isMobile }, testInfo) => {
        const account = await makeAccount('reader', testInfo);
        await page.goto('./account');
        await expect(page).toHaveURL(/\/login\?return=%2Faccount$/);

        await signIn(page, isMobile, account.userName, 'not-the-password');
        await expect(page.getByRole('alert')).toHaveText('Wrong name or password.');

        await page.getByLabel('Password', { exact: true }).fill(account.password);
        await press(page.getByRole('button', { name: 'Sign in' }), isMobile);

        await expect(page.getByRole('heading', { level: 1 })).toHaveText('My account');
        await expect(page.getByText(`Signed in as ${account.userName}`)).toBeVisible();
        // A plain account: no administration.
        await expect(page.locator('#main-nav').getByRole('link', { name: 'Administration' })).toHaveCount(0);
    });

    test('signing out ends the session @desktop', async ({ page, isMobile }, testInfo) => {
        const account = await makeAccount('leaver', testInfo);
        await page.goto('./');
        await signIn(page, isMobile, account.userName, account.password);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome to Librarion');

        await press(page.getByRole('link', { name: `My account (${account.userName})` }), isMobile);
        await press(page.getByRole('button', { name: 'Sign out' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');

        await page.goto('./');
        await expect(page).toHaveURL(/\/login/);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');
    });

    test('a server administrator recovers the account with the code the server shows @desktop', async ({ page, isMobile }, testInfo) => {
        const account = await makeAccount('rescued', testInfo, { serverAdmin: true });
        await page.goto('./login');
        await press(page.getByRole('link', { name: 'Recover the account' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Recover the account');

        await page.getByLabel('Name', { exact: true }).fill(account.userName);
        await press(page.getByRole('button', { name: 'Show a recovery code on the server' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: "server's console" })).toBeVisible();

        // What the person running the server reads in its console, or in this file beside the setup code.
        const file = path.join(data, 'recovery-code.txt');
        const line = fs.readFileSync(file, 'utf8').split(/\r?\n/).find(text => text.startsWith(`${account.userName}: `));
        const code = line?.match(/^\S+: (\S+)/)?.[1];
        expect(code, `a code for ${account.userName} in ${file}`).toBeTruthy();

        await page.getByLabel('Recovery code').fill(code!);
        await page.getByLabel('New password', { exact: true }).fill('recovered-password-1');
        await page.getByLabel('New password again').fill('recovered-password-1');
        await press(page.getByRole('button', { name: 'Set the password and sign in' }), isMobile);

        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome to Librarion');
    });
});
