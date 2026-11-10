import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ImportedField, ImportedTag } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { ImportedTagsSection } from './imported-tags-section';

const FIELDS: ImportedField[] = [
    { id: null, key: null, name: '', builtIn: false, tags: 3, capped: false },
    { id: 12, key: 'category', name: 'category', builtIn: true, tags: 10_000, capped: true },
];

const tag = (overrides: Partial<ImportedTag> = {}): ImportedTag => ({
    id: 7, name: 'Fantasy', color: null, books: 1234, reviewed: false,
    names: [{ name: 'fantasy', source: 'fb2', reviewed: false }, { name: 'Fantasy - General', source: 'calibre', reviewed: false }], ...overrides,
});

function setUp(canMap = true): ApiMock {
    const api = mockApi();
    api.libraries.mockResolvedValue([{ id: 1, tenantId: 1, name: 'Home', created: '' }, { id: 2, tenantId: 1, name: 'Work', created: '' }]);
    api.importedContext.mockResolvedValue({ libraries: [1, 2], canChange: true, canMap });
    api.importedFields.mockResolvedValue(FIELDS);
    api.importedSummary.mockResolvedValue({ tags: 3, links: 2000, topCount: 500, top: 2000, reviewed: 500 });
    return api;
}

async function render(api: ApiMock, page = 'names') {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(['library.importedTags', 'tenant.tags']));
    const fixture = TestBed.createComponent(ImportedTagsSection);
    fixture.componentRef.setInput('page', page);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { fixture, element, stable: async (ms = 0) => { await settle(ms); await fixture.whenStable(); } };
}

const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;

const choose = (element: HTMLElement, id: string, value: string) => {
    const select = element.querySelector<HTMLSelectElement>(`#${id}`)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
};

describe('ImportedTagsSection', () => {
    it('lists a library\'s imported tags by books with their sources and raw names, the coverage, and pages by key', async () => {
        const api = setUp();
        api.importedTags.mockResolvedValueOnce({ items: [tag(), tag({ id: 8, name: 'Horror', books: 500, names: [{ name: 'horror', source: 'fb2', reviewed: true }] })], next: 'n1' })
            .mockResolvedValueOnce({ items: [tag({ id: 9, name: 'Junk', books: 1, names: [] })], next: null })
            .mockResolvedValue({ items: [], next: null });
        const { element, stable } = await render(api);

        expect(api.importedTags).toHaveBeenCalledWith({ library: 1, field: null, source: null, status: 'review', after: null, limit: 50 });
        const rows = [...element.querySelectorAll('.imported-rows .row')];
        expect(rows[0].textContent).toContain('1,234 books · from FB2, Calibre · as “Fantasy - General”');
        expect(rows[1].textContent).not.toContain(' as ');
        expect(element.querySelector('.imported-summary')?.textContent).toContain('3 tags with 2,000 imported links in this library: the first 3 hold 100 %, the reviewed ones 25 %.');
        expect([...element.querySelectorAll('#imported-field option')].map(o => o.textContent?.trim())).toEqual(['Plain tags (3)', 'Categories (10,000+)']);

        button(element, 'More').click();
        await stable();
        expect(api.importedTags).toHaveBeenLastCalledWith(expect.objectContaining({ after: 'n1' }));
        expect(element.querySelectorAll('.imported-rows .row')).toHaveLength(3);

        choose(element, 'imported-library', '2');
        choose(element, 'imported-field', 'category');
        choose(element, 'imported-source', 'calibre');
        choose(element, 'imported-status', 'all');
        await stable();
        expect(api.importedTags).toHaveBeenLastCalledWith({ library: 2, field: 'category', source: 'calibre', status: 'all', after: null, limit: 50 });
        expect(api.importedSummary).toHaveBeenLastCalledWith(2, 'category', 'calibre');
        expect(element.textContent).toContain('No imported tags here.');
    });

    it('keeps the chosen tags, and ignores them after saying what goes; a big ignore is followed as a run', async () => {
        const api = setUp();
        api.importedTags.mockResolvedValue({ items: [tag(), tag({ id: 8, name: 'Junk', books: 6 })], next: null });
        api.reviewImported.mockResolvedValue({ changed: 2 });
        api.ignoreImported.mockResolvedValue({ runId: 33, logId: null, links: 1240 });
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const follow = vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const toast = vi.spyOn(ToastService.prototype, 'show');
        const { element, stable } = await render(api);

        expect(element.querySelector('.imported-bar')).toBeNull();
        element.querySelectorAll<HTMLInputElement>('.row-check').forEach(box => box.click());
        await stable();
        expect(element.querySelector('.imported-bar')?.textContent).toContain('2 selected');
        button(element, 'Keep').click();
        await stable();
        expect(api.reviewImported).toHaveBeenCalledWith([7, 8], true);
        expect(element.querySelector('.imported-bar')).toBeNull();

        element.querySelectorAll<HTMLInputElement>('.row-check').forEach(box => box.click());
        await stable();
        button(element, 'Ignore').click();
        await stable();
        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ title: 'Ignore the chosen tags (2)?', message: expect.stringContaining('1,240 books here'), danger: true }));
        expect(api.ignoreImported).toHaveBeenCalledWith([7, 8]);
        expect(follow).toHaveBeenCalledWith(33);
        expect(toast).toHaveBeenCalledWith(expect.stringContaining('Runs in the background'));

        // The run ends: the list is read again.
        const asked = api.importedTags.mock.calls.length;
        TestBed.inject(TaskWatch).ended.set([{ id: 33 } as never]);
        await stable();
        expect(api.importedTags.mock.calls.length).toBe(asked + 1);
    });

    it('maps the chosen tags onto one found with the picker', async () => {
        const api = setUp();
        api.importedTags.mockResolvedValue({ items: [tag()], next: null });
        api.pickTags.mockResolvedValue({ items: [{ id: 3, name: 'Fantasy fiction', color: null, field: null, shorthand: null, alias: null, parent: null, books: 9 }], exact: false });
        api.mapImported.mockResolvedValue({ runId: null, logId: 4, links: 1234 });
        const { element, stable } = await render(api);

        element.querySelector<HTMLInputElement>('.row-check')!.click();
        await stable();
        button(element, 'Map to…').click();
        await stable();
        const find = element.querySelector<HTMLInputElement>('#imported-map-target')!;
        find.value = 'fant';
        find.dispatchEvent(new Event('input'));
        await stable(250);
        expect(api.pickTags).toHaveBeenLastCalledWith(null, 'fant', 10);
        element.querySelector<HTMLElement>('[role="option"]')!.click();
        await stable();
        expect(element.textContent).toContain('Tags chosen: 1. Each merges into Fantasy fiction: their 1,234 books here');

        [...element.querySelectorAll<HTMLButtonElement>('lib-imported-map button')].find(b => b.textContent?.trim() === 'Map')!.click();
        await stable();
        expect(api.mapImported).toHaveBeenCalledWith([7], 3);
    });

    it('offers no Map without the right to manage tags', async () => {
        const api = setUp(false);
        api.importedTags.mockResolvedValue({ items: [tag()], next: null });
        const { element, stable } = await render(api);

        element.querySelector<HTMLInputElement>('.row-check')!.click();
        await stable();
        expect(button(element, 'Map to…')).toBeUndefined();
        expect(button(element, 'Ignore')).toBeDefined();
    });

    it('lists the ignores with Undo', async () => {
        const api = setUp();
        api.ignoredTags.mockResolvedValue([{ id: 5, created: '2026-11-06T10:00:00Z', tagId: 8, tagName: 'Junk', names: ['junk', 'JUNK!'], links: 12, completed: true }]);
        api.undoIgnore.mockResolvedValue({ runId: null, logId: 5, links: 12 });
        vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const { element, stable } = await render(api, 'ignored');

        expect(element.querySelector('.row')?.textContent).toContain('“junk”, “JUNK!” · 12 links taken off');
        button(element, 'Undo').click();
        await stable();
        expect(api.undoIgnore).toHaveBeenCalledWith(5);
        expect(api.ignoredTags).toHaveBeenCalledTimes(2);
    });
});
