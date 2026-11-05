// Editing a book on its page, on every device (stage 6, chunk 6): the title changed, the authors reordered (dragged by
// the handle with a mouse, Move up on a phone), a tag made by name with the picker; saved, the page shows it, and it
// stays after a reload.
import fs from 'node:fs';
import path from 'node:path';
import { uniqueName } from '../fixtures/accounts';
import { scannedLibrary } from '../fixtures/library';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';

function twoAuthors(title: string, first: string, second: string): Buffer {
    const author = (name: string) => `<author><first-name>${name.split(' ')[0]}</first-name><last-name>${name.split(' ')[1]}</last-name></author>`;
    return Buffer.from(`<?xml version="1.0" encoding="utf-8"?>
<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><description><title-info><genre>sf_fantasy</genre>
${author(first)}${author(second)}<book-title>${title}</book-title><lang>en</lang></title-info></description>
<body><section><p>${'Once upon a time. '.repeat(50)}</p></section></body></FictionBook>`, 'utf8');
}

test.describe('@browse', () => {
    test.use({ allowedErrors: /api\/tasks\/running due to access control checks/ });

    test('a book is edited on its page: title, author order, a new tag @desktop', async ({ page, isMobile }, testInfo) => {
        const name = uniqueName('Book edit', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const [first, second] = [`Ann Quill${suffix}`, `Bob Inkwell${suffix}`];
        const tag = `Shelf ${suffix}`;
        await scannedLibrary(page, name, folder => fs.writeFileSync(path.join(folder, 'omens.fb2'), twoAuthors('Teh Omens', first, second)));

        await press(page.locator('.book-link', { hasText: 'Teh Omens' }), isMobile);
        await expect(page.locator('.book-by a')).toHaveText([first, second]);
        const cover = await page.locator('.book-page-cover img').getAttribute('src');
        await press(page.getByRole('button', { name: 'Edit', exact: true }), isMobile);
        const editor = page.getByRole('dialog', { name: 'Edit the book' });
        await expect(editor).toBeVisible();

        await editor.getByLabel('Title', { exact: true }).fill('The Omens');
        const authors = editor.locator('ol[data-field="author"] .book-edit-value');
        if (isMobile) {
            await press(editor.getByRole('button', { name: `Move ${second} up` }), isMobile);
        } else {
            // Dragged by its handle above the first.
            const handle = authors.nth(1).locator('.book-edit-handle');
            const target = await authors.nth(0).boundingBox();
            const box = (await handle.boundingBox())!;
            await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); // mouse: the drag with a mouse, where there is one (phones use Move up)
            await page.mouse.down(); // mouse: the drag with a mouse, where there is one (phones use Move up)
            await page.mouse.move(box.x + box.width / 2, target!.y + 4, { steps: 8 }); // mouse: the drag with a mouse, where there is one (phones use Move up)
            await page.mouse.up(); // mouse: the drag with a mouse, where there is one (phones use Move up)
        }
        await expect(authors.locator('.book-edit-name')).toHaveText([second, first]);

        const add = editor.getByRole('combobox', { name: 'Add a tag' });
        await add.fill(tag);
        await press(editor.getByRole('option', { name: `Create “${tag}”` }), isMobile);
        await expect(editor.locator('.tag-chips .chip')).toHaveText([tag]);

        await press(editor.getByRole('button', { name: 'Save' }), isMobile);
        await expect(editor).toHaveCount(0);
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('The Omens');
        await expect(page.locator('.book-by a')).toHaveText([second, first]);
        await expect(page.locator('.book-fields .chip', { hasText: tag })).toBeVisible();
        // The generated cover shows the new title: its address changed (the browser keeps a cover for a day).
        await expect(page.locator('.book-page-cover img')).not.toHaveAttribute('src', cover!);

        await page.reload();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('The Omens');
        await expect(page.locator('.book-by a')).toHaveText([second, first]);
    });
});
