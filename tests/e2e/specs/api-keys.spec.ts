// API keys on every device (stage 7 chunks 4 and 8b, agents/docs/features/opds-auth.md): an account makes a key on My
// account › API keys that may be used in the address, sees it once with the catalog's address holding it, a reader app
// opens the catalog with it as the password and from that address alone, and once revoked both are refused. axe audits the page with the key shown, in both themes, once per run.
import { request } from '@playwright/test';
import { asAdmin, makeAccount, signIn } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { baseURL, signedOut } from '../paths';

/** The catalog's root as a reader app asks for it: Basic with the name and the key, no cookie. */
async function catalogStatus(userName: string, key: string): Promise<number> {
    const app = await request.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Basic ${Buffer.from(`${userName}:${key}`).toString('base64')}` } });
    try {
        return (await app.get('opds')).status();
    } finally {
        await app.dispose();
    }
}

/** The catalog as a reader app that can't sign in asks for it: the address holding the key, nothing else. */
async function addressStatus(address: string): Promise<number> {
    const app = await request.newContext();
    try {
        return (await app.get(address)).status();
    } finally {
        await app.dispose();
    }
}

test.describe('@api-keys', () => {
    test.use({ storageState: signedOut });

    test('a key is made, shown once, opens the catalog and ends when revoked @desktop', async ({ page, isMobile }, testInfo) => {
        const account = await makeAccount('keys', testInfo);
        await page.goto('./admin/account/apiKeys');
        await signIn(page, isMobile, account.userName, account.password);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('API keys');
        await expect(page.getByText(`the user name ${account.userName}`)).toBeVisible();

        await press(page.getByRole('button', { name: 'New key' }), isMobile);
        await page.getByLabel('Name', { exact: true }).fill('Phone');
        await press(page.getByRole('checkbox', { name: /Can be used in the address/ }), isMobile);
        await press(page.getByRole('button', { name: 'Make the key' }), isMobile);
        const shown = page.getByLabel('Key', { exact: true });
        await expect(shown).toHaveValue(/^lib_\d+_[A-Za-z0-9_-]{43}$/);
        const key = await shown.inputValue();
        const address = page.getByLabel('Catalog address with the key');
        await expect(address).toHaveValue(new URL(`opds/k/${key}`, baseURL).href);
        await expect(page.getByRole('list', { name: 'API keys' }).getByRole('listitem').filter({ hasText: 'Phone' })).toContainText('Reader apps (OPDS)');
        if (testInfo.project.name === 'android') {
            await audit(page, 'api keys (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'api keys (light)');
        }

        expect(await catalogStatus(account.userName, key)).toBe(200);
        const keyAddress = await address.inputValue();
        expect(await addressStatus(keyAddress)).toBe(200);
        const listed = await asAdmin<{ name: string }[]>('GET', `api/users/${account.id}/api-keys`);
        expect(listed.map(k => k.name)).toEqual(['Phone']);

        await press(page.getByRole('button', { name: 'Done' }), isMobile);
        await expect(shown).toHaveCount(0);
        await press(page.getByRole('button', { name: 'Revoke' }), isMobile);
        await press(page.getByRole('dialog', { name: 'Revoke the key?' }).getByRole('button', { name: 'Revoke' }), isMobile);
        await expect(page.getByRole('list', { name: 'API keys' }).getByRole('listitem').filter({ hasText: 'Phone' })).toContainText('Revoked');

        expect(await catalogStatus(account.userName, key)).toBe(401);
        expect(await addressStatus(keyAddress)).toBe(401);
    });
});
