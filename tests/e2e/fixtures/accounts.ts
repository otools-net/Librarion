// Accounts for the specs: made through the API as the administrator (the first start's), one per test with a name of
// its own, so tests running at once on every device never share one; and signing in through the page, by finger.
import { request, type Page, type TestInfo } from '@playwright/test';
import { adminState, baseURL } from '../paths';
import { expect } from './test';
import { press } from './press';

export interface Account {
    id: number;
    userName: string;
    password: string;
}

/** A name no other test uses: `<prefix>-<device>-<random>`. */
export function uniqueName(prefix: string, testInfo: TestInfo): string {
    return `${prefix}-${testInfo.project.name}-${Math.random().toString(36).slice(2, 8)}`;
}

/** Calls the API as the administrator; throws on a refusal, with the server's answer. */
export async function asAdmin<T>(method: string, url: string, data?: unknown): Promise<T> {
    const api = await request.newContext({ baseURL, storageState: adminState, extraHTTPHeaders: { 'X-Librarion-Request': '1' } });
    try {
        const response = await api.fetch(url, { method, data });
        const text = await response.text();
        if (!response.ok())
            throw new Error(`${method} ${url}: ${response.status()} ${text}`);
        return (text ? JSON.parse(text) : null) as T;
    } finally {
        await api.dispose();
    }
}

/** A new account in the administrator's tenant, in its built-in Users group; or a server administrator. */
export async function makeAccount(prefix: string, testInfo: TestInfo, options: { serverAdmin?: boolean } = {}): Promise<Account> {
    const userName = uniqueName(prefix, testInfo);
    const password = `${prefix}-password-1`;
    let groupIds: number[] = [];
    if (!options.serverAdmin) {
        const groups = await asAdmin<{ id: number; builtIn: string | null }[]>('GET', 'api/groups');
        groupIds = groups.filter(group => group.builtIn === 'users').map(group => group.id);
        expect(groupIds, 'the built-in Users group').toHaveLength(1);
    }
    const user = await asAdmin<{ id: number }>('POST', 'api/users', { userName, password, isServerAdmin: !!options.serverAdmin, groupIds });
    return { id: user.id, userName, password };
}

/** Fills the sign-in page and sends it (the page must be showing it). */
export async function signIn(page: Page, isMobile: boolean, userName: string, password: string): Promise<void> {
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in');
    await page.getByLabel('Name', { exact: true }).fill(userName);
    await page.getByLabel('Password', { exact: true }).fill(password);
    await press(page.getByRole('button', { name: 'Sign in' }), isMobile);
}
