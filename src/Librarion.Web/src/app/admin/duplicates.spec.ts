import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { BookListItem, DuplicateGroupItem } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { ConfirmService } from '../shared/confirm';
import { DuplicatesSection } from './duplicates-section';

const book = (id: number, title: string): BookListItem =>
    ({ id, libraryId: 1, title, fileName: null, authors: ['Ann Writer'], series: null, seriesNumber: null, formats: ['fb2', 'epub'], coverSource: 'book' });
const group = (id: number, level: DuplicateGroupItem['level'], ...books: BookListItem[]): DuplicateGroupItem =>
    ({ id, level, scheme: level === 'identifier' ? 'isbn' : null, books, bookIds: books.map(b => b.id) });

async function render(api: ApiMock) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [...testProviders(api), provideRouter([])] });
    TestBed.inject(SessionService).set(sessionInfo(['library.merge']));
    const fixture = TestBed.createComponent(DuplicatesSection);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { element, stable: async () => { await settle(); await fixture.whenStable(); } };
}

const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;

function setUp(): ApiMock {
    const api = mockApi();
    api.libraries.mockResolvedValue([{ id: 1, tenantId: 1, name: 'Home', created: '' }, { id: 2, tenantId: 1, name: 'Work', created: '' }]);
    api.duplicateContext.mockResolvedValue({ libraries: [1, 2] });
    return api;
}

describe('DuplicatesSection', () => {
    it('lists a library\'s groups with why, pages by key, and filters by reason', async () => {
        const api = setUp();
        api.duplicates.mockResolvedValueOnce({ items: [group(7, 'identifier', book(1, 'Dune'), book(2, 'DUNE'))], next: 'n1', built: true })
            .mockResolvedValueOnce({ items: [group(8, 'work', book(3, 'Emma'), book(4, 'Emma'))], next: null, built: true })
            .mockResolvedValue({ items: [], next: null, built: true });
        const { element, stable } = await render(api);

        expect(api.duplicates).toHaveBeenCalledWith(1, null, null);
        expect(element.querySelector('.duplicate-reason')?.textContent).toBe('Same ISBN');
        expect(element.querySelectorAll('.duplicate-book')).toHaveLength(2);
        button(element, 'More').click();
        await stable();
        expect(api.duplicates).toHaveBeenLastCalledWith(1, null, 'n1');
        expect(element.querySelectorAll('.duplicate-group')).toHaveLength(2);

        const level = element.querySelector<HTMLSelectElement>('#duplicates-level')!;
        level.value = 'file';
        level.dispatchEvent(new Event('change'));
        await stable();
        expect(api.duplicates).toHaveBeenLastCalledWith(1, 'file', null);
        expect(element.textContent).toContain('No possible duplicates in this library.');
    });

    it('sets a group apart after asking, and finds them again as a run', async () => {
        const api = setUp();
        api.duplicates.mockResolvedValue({ items: [group(7, 'work', book(1, 'Dune'), book(2, 'Dune'))], next: null, built: false });
        api.notDuplicates.mockResolvedValue(undefined);
        api.buildDuplicates.mockResolvedValue({ runId: 70 });
        vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const follow = vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const { element, stable } = await render(api);

        expect(element.textContent).toContain('haven\'t been looked for yet');
        button(element, 'Not duplicates').click();
        await stable();
        expect(api.notDuplicates).toHaveBeenCalledWith([1, 2]);
        expect(element.querySelector('.duplicate-group')).toBeNull();

        button(element, 'Find again').click();
        await stable();
        expect(follow).toHaveBeenCalledWith(70);
    });
});
