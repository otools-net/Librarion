import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { ManagedFields, ManagedTag, TagDefinition } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { TagsSection } from './tags-section';

const FIELDS: ManagedFields = {
    fields: [
        { id: 3, key: 'author', name: 'author', builtIn: true, color: null, isHidden: false, values: 10_000, capped: true },
        { id: 40, key: 'genre', name: 'Genre', builtIn: false, color: null, isHidden: false, values: 2, capped: false },
    ],
    plainTags: 2, plainCapped: false,
};

const tag = (overrides: Partial<ManagedTag> = {}): ManagedTag => ({
    id: 7, name: 'Fantasy', sortName: 'Fantasy', color: '#aa0000', isCategory: false, isHidden: false, builtIn: false, books: 1234, children: 3, ...overrides,
});

const definition = (overrides: Partial<TagDefinition> = {}): TagDefinition => ({
    id: 7, name: 'Fantasy', sortName: 'Fantasy', shorthand: null, color: '#aa0000', isCategory: false, isHidden: false, disambiguationParentId: null,
    fieldId: null, fieldKey: null, builtIn: false, aliases: ['High fantasy'], parents: [{ id: 2, name: 'Fiction' }], books: 1234, ...overrides,
});

async function render(api: ApiMock, page = 'tags') {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(['tenant.tags']));
    const fixture = TestBed.createComponent(TagsSection);
    fixture.componentRef.setInput('page', page);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { fixture, element, stable: async (ms = 0) => { await settle(ms); await fixture.whenStable(); } };
}

const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;

describe('TagsSection', () => {
    it('lists the plain tags with their books and children, the fields with their counts, and pages by key', async () => {
        const api = mockApi();
        api.managedFields.mockResolvedValue(FIELDS);
        api.managedTags.mockResolvedValueOnce({ items: [tag(), tag({ id: 8, name: 'Archived', books: 0, children: 0, isHidden: true, builtIn: true, color: null })], next: 'n1', capped: false })
            .mockResolvedValueOnce({ items: [tag({ id: 9, name: 'Horror', children: 0 })], next: null, capped: false });
        const { element, stable } = await render(api);

        const options = [...element.querySelectorAll('#tags-field option')].map(o => o.textContent?.trim());
        expect(options).toEqual(['Plain tags (2)', 'Authors (10,000+)', 'Genre (2)']);
        const rows = [...element.querySelectorAll('.tag-rows .row')];
        expect(rows[0].textContent).toContain('1,234 books · 3 tags under it');
        expect(rows[1].textContent).toContain('Hidden');
        expect(rows[1].textContent).toContain('Built-in');
        expect(api.managedTags).toHaveBeenCalledWith(null, { q: null, after: null, limit: 50 });

        button(element, 'More').click();
        await stable();
        expect(api.managedTags).toHaveBeenLastCalledWith(null, { q: null, after: 'n1', limit: 50 });
        expect(element.querySelectorAll('.tag-rows .row')).toHaveLength(3);
        expect(button(element, 'More')).toBeUndefined();
    });

    it('shows a field\'s values, found by what is typed, and offers to merge a field that isn\'t built in', async () => {
        const api = mockApi();
        api.managedFields.mockResolvedValue(FIELDS);
        api.managedTags.mockResolvedValue({ items: [tag()], next: null, capped: true });
        const { element, stable } = await render(api);

        const select = element.querySelector<HTMLSelectElement>('#tags-field')!;
        select.value = 'author';
        select.dispatchEvent(new Event('change'));
        await stable();
        expect(api.managedTags).toHaveBeenLastCalledWith('author', { q: null, after: null, limit: 50 });
        expect(button(element, 'New value')).toBeDefined();
        expect(button(element, 'Merge field…')).toBeUndefined();

        const search = element.querySelector<HTMLInputElement>('.toolbar-search')!;
        search.value = 'king';
        search.dispatchEvent(new Event('input'));
        await stable(300);
        expect(api.managedTags).toHaveBeenLastCalledWith('author', { q: 'king', after: null, limit: 50 });
        expect(element.textContent).toContain('The first 1 found');

        select.value = 'genre';
        select.dispatchEvent(new Event('change'));
        await stable();
        expect(button(element, 'Merge field…')).toBeDefined();
    });

    it('edits a tag: its names, parents and flags saved, the server\'s refusals shown by field', async () => {
        const api = mockApi();
        api.managedFields.mockResolvedValue(FIELDS);
        api.managedTags.mockResolvedValue({ items: [tag()], next: null, capped: false });
        api.tagDefinition.mockResolvedValue(definition());
        api.updateTag.mockRejectedValueOnce(new ApiError(400, 'x', { parentIds: ['parent.cycle'] }))
            .mockResolvedValueOnce(definition({ name: 'Fantasy fiction' }));
        const { element, stable } = await render(api);

        element.querySelector<HTMLButtonElement>('.tag-rows .row-main')!.click();
        await stable();
        expect(element.querySelector('.sheet-subtitle')?.textContent).toContain('1234 books');
        expect(element.querySelector<HTMLTextAreaElement>('#tag-aliases')!.value).toBe('High fantasy');
        expect(element.querySelector('.tag-chips')?.textContent).toContain('Fiction');

        const name = element.querySelector<HTMLInputElement>('#tag-name')!;
        name.value = 'Fantasy fiction';
        name.dispatchEvent(new Event('input'));
        element.querySelector('form.tag-editor')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(element.querySelector('.field-error')?.textContent).toContain('under itself');

        element.querySelector('form.tag-editor')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(api.updateTag).toHaveBeenLastCalledWith(7, expect.objectContaining({ name: 'Fantasy fiction', aliases: ['High fantasy'], parentIds: [2], color: '#aa0000' }));
        expect(api.managedTags).toHaveBeenCalledTimes(2);
    });

    it('deletes a tag after saying how many books lose it; a big delete is followed as a run', async () => {
        const api = mockApi();
        api.managedFields.mockResolvedValue(FIELDS);
        api.managedTags.mockResolvedValue({ items: [tag()], next: null, capped: false });
        api.tagDefinition.mockResolvedValue(definition());
        api.deleteTag.mockResolvedValue({ runId: 51, logId: null, links: 1234 });
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const follow = vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const toast = vi.spyOn(ToastService.prototype, 'show');
        const { element, stable } = await render(api);

        element.querySelector<HTMLButtonElement>('.tag-rows .row-main')!.click();
        await stable();
        button(element, 'Delete').click();
        await stable();

        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('1,234 books lose Fantasy'), danger: true }));
        expect(api.deleteTag).toHaveBeenCalledWith(7);
        expect(follow).toHaveBeenCalledWith(51);
        expect(toast).toHaveBeenCalledWith(expect.stringContaining('Runs in the background'));
    });

    it('merges a tag into one found by name, saying what moves first', async () => {
        const api = mockApi();
        api.managedFields.mockResolvedValue(FIELDS);
        api.managedTags.mockResolvedValueOnce({ items: [tag()], next: null, capped: false })
            .mockResolvedValueOnce({ items: [tag(), tag({ id: 9, name: 'Fantasy (genre)' })], next: null, capped: false })
            .mockResolvedValue({ items: [], next: null, capped: false });
        api.tagDefinition.mockResolvedValue(definition());
        api.mergeTag.mockResolvedValue({ runId: null, logId: 4, links: 1234 });
        const { element, stable } = await render(api);

        element.querySelector<HTMLButtonElement>('.tag-rows .row-main')!.click();
        await stable();
        button(element, 'Merge').click();
        await stable();
        const find = element.querySelector<HTMLInputElement>('#merge-target')!;
        find.value = 'fant';
        find.dispatchEvent(new Event('input'));
        await stable(250);
        const options = [...element.querySelectorAll<HTMLElement>('[role="option"]')];
        expect(options.map(o => o.textContent)).toEqual([expect.stringContaining('Fantasy (genre)')]);
        options[0].click();
        await stable();
        expect(element.textContent).toContain('1234 books of Fantasy move to Fantasy (genre)');

        [...element.querySelectorAll<HTMLButtonElement>('lib-tag-merge button')].find(b => b.textContent?.trim() === 'Merge')!.click();
        await stable();
        expect(api.mergeTag).toHaveBeenCalledWith(7, 9);
    });

    it('lists the merges with Undo, and says why one can\'t be undone', async () => {
        const api = mockApi();
        api.merges.mockResolvedValue([
            { id: 5, kind: 'field', created: '2026-11-05T10:00:00Z', sourceId: 40, sourceName: 'genre', targetId: 9, targetName: 'category', links: 12, completed: true, undone: null },
            { id: 4, kind: 'tag', created: '2026-11-05T09:00:00Z', sourceId: 7, sourceName: 'Steven King', targetId: 8, targetName: 'Stephen King', links: 3, completed: true, undone: '2026-11-05T09:30:00Z' },
        ]);
        api.undoMerge.mockRejectedValue(new ApiError(409, 'x', null, 'merge.later'));
        vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const { element, stable } = await render(api, 'merges');

        const rows = [...element.querySelectorAll('.merge-row')];
        expect(rows[0].textContent).toContain('genre → category');
        expect(rows[0].textContent).toContain('Field · 12 links');
        expect(rows[1].textContent).toContain('Undone');
        expect(rows[1].querySelector('button')).toBeNull();

        rows[0].querySelector<HTMLButtonElement>('button')!.click();
        await stable();
        expect(api.undoMerge).toHaveBeenCalledWith(5);
        expect(element.querySelector('[role="alert"]')?.textContent).toContain('undo that one first');
    });
});
