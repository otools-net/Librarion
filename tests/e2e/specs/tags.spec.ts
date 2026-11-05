// The tag manager on every device (Administration › Catalog › Tags, stage 6): the administrator makes two tags, puts
// one under the other, merges one into the other after the dialog says what moves, and undoes the merge under Merges.
// The run's devices share a server: every name is the test's own and the lists are searched for it.
import { uniqueName } from '../fixtures/accounts';
import { press } from '../fixtures/press';
import { expect, test } from '../fixtures/test';

test.describe('@tags', () => {
    test('tags are made, put under one another, merged and the merge undone @desktop', async ({ page, isMobile }, testInfo) => {
        const parent = uniqueName('Shelf', testInfo);
        const child = uniqueName('Shelf child', testInfo);

        await page.goto('./admin/tags/tags');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Tags');
        for (const name of [parent, child]) {
            await press(page.getByRole('button', { name: 'New tag' }), isMobile);
            const editor = page.getByRole('dialog', { name: 'New tag' });
            await editor.getByLabel('Name', { exact: true }).fill(name);
            await press(editor.getByRole('button', { name: 'Save' }), isMobile);
            await expect(editor).toBeHidden();
        }

        // The child under the parent, found by name in the editor.
        const find = page.getByRole('searchbox', { name: 'Find a tag' });
        await find.fill(child);
        const list = page.getByRole('list', { name: 'Tag list' });
        await press(list.getByRole('button', { name: child }), isMobile);
        const editor = page.getByRole('dialog', { name: child });
        await editor.getByRole('combobox', { name: 'Put it under a tag' }).fill(parent);
        await press(editor.getByRole('option').filter({ hasText: parent }), isMobile);
        await expect(editor.locator('.tag-chips')).toContainText(parent);
        await editor.getByLabel('Other names').fill(`${child} alias`);
        await press(editor.getByRole('button', { name: 'Save' }), isMobile);
        await expect(editor).toBeHidden();

        await find.fill(parent);
        await expect(list.getByRole('listitem').filter({ hasText: parent }).first()).toContainText('1 tags under it');

        // The child merged into the parent: the dialog says what moves before the button.
        await find.fill(child);
        await press(list.getByRole('button', { name: child }), isMobile);
        await press(page.getByRole('dialog', { name: child }).getByRole('button', { name: 'Merge' }), isMobile);
        const merge = page.getByRole('dialog', { name: `Merge ${child}` });
        await merge.getByRole('combobox', { name: 'Merge into' }).fill(parent);
        await press(merge.getByRole('option').filter({ hasText: parent }), isMobile);
        await expect(merge).toContainText(`becomes another name of ${parent}`);
        await press(merge.getByRole('button', { name: 'Merge', exact: true }), isMobile);
        await expect(page.getByRole('status').filter({ hasText: 'Merged.' })).toBeVisible();
        await expect(list.getByRole('button', { name: child })).toHaveCount(0);

        // Merges: the newest first, undone after a question.
        await page.goto('./admin/tags/merges');
        const row = page.getByRole('list', { name: 'Merges' }).getByRole('listitem').filter({ hasText: `${child} → ${parent}` });
        await press(row.getByRole('button', { name: 'Undo' }), isMobile);
        await press(page.getByRole('dialog', { name: 'Undo the merge?' }).getByRole('button', { name: 'Undo' }), isMobile);
        await expect(row).toContainText('Undone');

        await page.goto('./admin/tags/tags');
        await page.getByRole('searchbox', { name: 'Find a tag' }).fill(child);
        await expect(page.getByRole('list', { name: 'Tag list' }).getByRole('button', { name: child })).toBeVisible();
        await page.evaluate(() => scrollTo({ top: 0 }));
    });
});
