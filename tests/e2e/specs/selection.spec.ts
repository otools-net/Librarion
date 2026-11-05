// Selecting books and tagging them, on every device (stage 6, chunk 7): two books selected (Ctrl-click with a mouse; a
// long press then a tap on a phone), a tag made and given to both from the selection bar; then Tag all gives another
// tag to every book of the list after counting them. The books' pages show the tags.
import fs from 'node:fs';
import path from 'node:path';
import type { Locator } from '@playwright/test';
import { uniqueName } from '../fixtures/accounts';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';

const headers = { 'X-Librarion-Request': '1' };

/** A finger resting on the card: the list's own long press (pointer events; iOS gives links no `contextmenu`). */
async function longPress(target: Locator): Promise<void> {
    const box = (await target.boundingBox())!;
    const at = { clientX: box.x + box.width / 2, clientY: box.y + box.height / 2, pointerType: 'touch', isPrimary: true, bubbles: true };
    await target.dispatchEvent('pointerdown', at);
    await target.page().waitForTimeout(700);
    await target.dispatchEvent('pointerup', at);
}

test.describe('@browse', () => {
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('books are selected and tagged, then every book of the list @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Selection', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const [picked, all] = [`Picked ${suffix}`, `Everything ${suffix}`];
        await scannedLibrary(page, name, folder => {
            for (const title of ['Alpha', 'Beta', 'Gamma'])
                fs.writeFileSync(path.join(folder, `${title}.fb2`), fb2(title, `Ann Quill${suffix}`));
        });
        const card = (title: string) => page.locator('.book', { hasText: title });
        await expect(card('Gamma')).toBeVisible();

        if (isMobile) {
            await longPress(card('Alpha').locator('.book-link'));
            await expect(card('Alpha')).toHaveClass(/selected/);
            await card('Beta').locator('.book-link').tap();
        } else {
            await card('Alpha').locator('.book-link').click({ modifiers: ['ControlOrMeta'] }); // mouse: Ctrl-click is the desktop's way
            await card('Beta').locator('.book-select').click(); // mouse: its box, with the mouse
        }
        await expect(page).not.toHaveURL(/\/books\//);
        const bar = page.getByRole('region', { name: 'Selected books' });
        await expect(bar.getByRole('status')).toHaveText('2 selected');

        await press(bar.getByRole('button', { name: 'Tags' }), isMobile);
        const dialog = page.getByRole('dialog', { name: 'Tags of 2 books' });
        await dialog.getByRole('combobox', { name: 'Add a tag' }).fill(picked);
        await press(dialog.getByRole('option', { name: `Create “${picked}”` }), isMobile);
        await expect(dialog.getByRole('checkbox', { name: `${picked}: all of them have it` })).toBeChecked();
        await press(dialog.getByRole('button', { name: 'Save' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: 'Tags saved.' })).toBeVisible();
        await expect(bar).toHaveCount(0);

        // Every book of the list, counted first.
        await press(page.getByRole('button', { name: 'Tag all' }), isMobile);
        const scope = page.getByRole('dialog', { name: 'Tag every book of the list' });
        await expect(scope).toContainText('This list has 3 books.');
        await scope.getByRole('combobox', { name: 'Add a tag to give' }).fill(all);
        await press(scope.getByRole('option', { name: `Create “${all}”` }), isMobile);
        await press(scope.getByRole('button', { name: 'Apply to 3 books' }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: '3 tags changed.' })).toBeVisible();

        // The books' pages say so.
        await press(card('Alpha').locator('.book-link'), isMobile);
        await expect(page.locator('.book-fields .chip', { hasText: picked })).toBeVisible();
        await expect(page.locator('.book-fields .chip', { hasText: all })).toBeVisible();
        const gamma = await page.request.get(`api/books?library=${await page.evaluate(() => localStorage.getItem('librarion.library'))}&q=${encodeURIComponent(`tag:"${picked}"`)}`, { headers });
        expect((await gamma.json()).items.map((b: { title: string }) => b.title).sort()).toEqual(['Alpha', 'Beta']);
    });
});
