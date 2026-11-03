// A library of the test's own: its folder under <work>/books (written by `write`), added and scanned through the API,
// then chosen in the switcher (the page reloaded). Each device and test gets its own, so they never meet.
import fs from 'node:fs';
import path from 'node:path';
import type { Page } from '@playwright/test';
import { expect } from './test';
import { work } from '../paths';

const headers = { 'X-Librarion-Request': '1' };

export async function scannedLibrary(page: Page, name: string, write: (folder: string) => void): Promise<number> {
    const folder = path.join(work, 'books', name);
    fs.rmSync(folder, { recursive: true, force: true });
    fs.mkdirSync(folder, { recursive: true });
    write(folder);

    await page.goto('./');
    const api = page.request;
    const library = await (await api.post('api/libraries', { headers, data: { name } })).json() as { id: number };
    expect((await api.post(`api/libraries/${library.id}/roots`, { headers, data: { path: folder } })).ok()).toBe(true);
    const { runId } = await (await api.post(`api/libraries/${library.id}/scan`, { headers })).json() as { runId: number };
    await expect.poll(async () => (await (await api.get(`api/tasks/${runId}`, { headers })).json()).outcome, { timeout: 30_000 }).toBe('succeeded');
    await page.evaluate(id => localStorage.setItem('librarion.library', String(id)), library.id);
    await page.reload();
    return library.id;
}
