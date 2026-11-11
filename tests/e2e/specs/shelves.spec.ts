// Shelves on every device (stage 7 chunk 10, agents/docs/features/shelves.md): a book put on a new shelf from its
// page, two more from a selection of the library; the shelf opened from the navigation shows them in its own order; a
// book moved to the start (dragged by its grip with a mouse, Move on a phone), still there after a reload; one taken
// off; the shelf renamed, then deleted, asking first. The phone layout of the page and its dialogs is checked, and axe
// audits the shelf's page in both themes once per run.
import fs from 'node:fs';
import path from 'node:path';
import type { Locator, Page } from '@playwright/test';
import { uniqueName } from '../fixtures/accounts';
import { audit } from '../fixtures/axe';
import { expectFitsScreen, expectTapTargets } from '../fixtures/layout';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, isPhoneWidth, test } from '../fixtures/test';
import { fb2 } from '../fixtures/zip';

/** A finger resting on the card: the list's own long press (pointer events; iOS gives links no `contextmenu`). */
async function longPress(target: Locator): Promise<void> {
    const box = (await target.boundingBox())!;
    const at = { clientX: box.x + box.width / 2, clientY: box.y + box.height / 2, pointerType: 'touch', isPrimary: true, bubbles: true };
    await target.dispatchEvent('pointerdown', at);
    await target.page().waitForTimeout(700);
    await target.dispatchEvent('pointerup', at);
}

/** The titles of the list, in order. */
const titles = (page: Page) => page.locator('li.book .book-title').allTextContents();

test.describe('@shelves', () => {
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('books are put on a shelf, reordered, taken off; the shelf renamed and deleted @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Shelf', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const shelf = `Reading ${suffix}`;
        await scannedLibrary(page, name, folder => {
            for (const title of ['Alpha', 'Beta', 'Gamma'])
                fs.writeFileSync(path.join(folder, `${title}.fb2`), fb2(title, `Ann Shelver${suffix}`));
        });
        const card = (title: string) => page.locator('.book', { hasText: title });
        await expect(card('Gamma')).toBeVisible();

        // A book's page: Add to shelf makes the shelf and puts the book on it.
        await press(card('Alpha').locator('.book-link'), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Alpha');
        await press(page.getByRole('button', { name: 'Add to shelf' }), isMobile);
        let dialog = page.getByRole('dialog', { name: 'Add to shelf' });
        await dialog.getByLabel('New shelf').fill(shelf);
        await press(dialog.getByRole('button', { name: 'Create and add' }), isMobile);
        await expect(dialog.getByRole('checkbox', { name: shelf })).toBeChecked();
        if (isPhoneWidth(page))
            await expectFitsScreen(page);
        await press(dialog.getByRole('button', { name: 'Done' }), isMobile);
        await expect(dialog).toHaveCount(0);

        // Two more from a selection of the library.
        await page.goto('./');
        await expect(card('Gamma')).toBeVisible();
        if (isMobile) {
            await longPress(card('Beta').locator('.book-link'));
            await expect(card('Beta')).toHaveClass(/selected/);
            await card('Gamma').locator('.book-link').tap();
        } else {
            await card('Beta').locator('.book-link').click({ modifiers: ['ControlOrMeta'] }); // mouse: Ctrl-click is the desktop's way
            await card('Gamma').locator('.book-select').click(); // mouse: its box, with the mouse
        }
        const bar = page.getByRole('region', { name: 'Selected books' });
        await expect(bar.getByRole('status')).toHaveText('2 selected');
        if (isPhoneWidth(page))
            await expectTapTargets(page, '.selection-bar');
        await press(bar.getByRole('button', { name: 'Add to shelf' }), isMobile);
        dialog = page.getByRole('dialog', { name: 'Add 2 books to shelves' });
        await press(dialog.getByRole('checkbox', { name: shelf }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: `Put 2 on ${shelf}.` })).toBeVisible();
        await press(dialog.getByRole('button', { name: 'Done' }), isMobile);

        // The shelf from the navigation (a sheet on phones), in its own order.
        if (isPhoneWidth(page))
            await press(page.getByRole('button', { name: 'Open the menu' }), isMobile);
        await press(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: new RegExp(`^${shelf}`) }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(shelf);
        await expect(page).toHaveURL(new RegExp(`/shelves/\\d+/${encodeURIComponent(shelf)}$`));
        await expect(page.locator('.browse-count')).toHaveText('3 books');
        await expect.poll(() => titles(page)).toEqual(['Alpha', 'Beta', 'Gamma']);
        if (isPhoneWidth(page)) {
            await expectFitsScreen(page);
            await expectTapTargets(page, '.books');
        }
        if (testInfo.project.name === 'android') {
            await audit(page, 'a shelf (dark)');
            await page.emulateMedia({ colorScheme: 'light' });
            await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
            await audit(page, 'a shelf (light)');
        }

        // Gamma to the start: dragged by its grip with a mouse, Move on a phone.
        const grip = card('Gamma').getByRole('button', { name: 'Move Gamma' });
        if (isMobile) {
            await press(grip, isMobile);
            const move = page.getByRole('dialog', { name: 'Move Gamma' });
            await expect(move.getByRole('status')).toHaveText('Place on the shelf: 3');
            if (isPhoneWidth(page))
                await expectFitsScreen(page);
            await press(move.getByRole('button', { name: 'To the start' }), isMobile);
            await expect(move.getByRole('status')).toHaveText('Place on the shelf: 1');
            await press(move.getByRole('button', { name: 'Done' }), isMobile);
        } else {
            // mouse: dragging is the desktop's way (the keys and Move are checked in the client's specs)
            const from = (await grip.boundingBox())!;
            const to = (await card('Alpha').boundingBox())!;
            await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
            await page.mouse.down();
            await page.mouse.move(to.x + 8, to.y + to.height / 2, { steps: 12 });
            await expect(card('Alpha')).toHaveClass(/drop-before/);
            await page.mouse.up();
        }
        await expect.poll(() => titles(page)).toEqual(['Gamma', 'Alpha', 'Beta']);
        await page.reload();
        await expect.poll(() => titles(page)).toEqual(['Gamma', 'Alpha', 'Beta']);

        // Alpha taken off the shelf (it stays in its library).
        if (isMobile)
            await longPress(card('Alpha').locator('.book-link'));
        else
            await card('Alpha').locator('.book-select').click(); // mouse: its box, with the mouse
        await press(bar.getByRole('button', { name: 'Remove from shelf' }), isMobile);
        await expect.poll(() => titles(page)).toEqual(['Gamma', 'Beta']);
        await expect(page.locator('.browse-count')).toHaveText('2 books');

        // Renamed: the address follows; then deleted, asking first.
        const renamed = `Done ${suffix}`;
        await press(page.getByRole('button', { name: 'Rename' }), isMobile);
        const rename = page.getByRole('dialog', { name: 'Rename' });
        await rename.getByLabel('Name').fill(renamed);
        await press(rename.getByRole('button', { name: 'Save' }), isMobile);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);
        await expect(page).toHaveURL(new RegExp(`/${encodeURIComponent(renamed)}$`));
        await press(page.getByRole('button', { name: 'Delete' }), isMobile);
        await press(page.getByRole('dialog', { name: `Delete ${renamed}?` }).getByRole('button', { name: 'Delete' }), isMobile);
        await expect(page).toHaveURL(/\/shelves$/);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Shelves');
        await expect(page.getByRole('link', { name: new RegExp(renamed) })).toHaveCount(0);
    });
});
