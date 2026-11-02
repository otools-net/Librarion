import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { BookListItem, KeysetPage } from '../core/models';
import { SessionService } from '../core/session.service';
import { bookWindow, gridColumns } from './book-window';
import { BrowsePage } from './browse-page';
import { BrowseState } from './browse-state';

const book = (id: number, overrides: Partial<BookListItem> = {}): BookListItem => ({
    id, libraryId: 5, title: `Book ${id}`, fileName: `book-${id}.fb2`, authors: ['Terry Pratchett'], series: null, seriesNumber: null,
    formats: ['fb2'], coverSource: 'generated', ...overrides,
});
const page = (from: number, count: number, next: string | null): KeysetPage<BookListItem> =>
    ({ items: Array.from({ length: count }, (_, i) => book(from + i)), next });

describe('bookWindow', () => {
    it('draws the rows in view and a few around them, the others as space', () => {
        // 1000 books, 4 per row (250 rows of 300 px), the list scrolled 30 000 px: rows 100 to 103 in an 800 px view, 2 more each side.
        const window = bookWindow(30_000, 800, 300, 4, 1000, 2);
        expect(window).toEqual({ start: 98 * 4, end: 106 * 4, before: 98 * 300, after: (250 - 106) * 300 });
    });

    it('starts at the top, ends at the last book, and draws everything until a row is measured', () => {
        expect(bookWindow(-200, 800, 300, 4, 10, 1)).toEqual({ start: 0, end: 10, before: 0, after: 0 });
        expect(bookWindow(50_000, 800, 300, 4, 10, 1).end).toBe(10);
        expect(bookWindow(0, 800, 0, 4, 500)).toEqual({ start: 0, end: 500, before: 0, after: 0 });
        expect(bookWindow(0, 800, 300, 4, 0)).toEqual({ start: 0, end: 0, before: 0, after: 0 });
    });

    it('fits as many cards per row as their least width allows', () => {
        expect(gridColumns(1000, 140, 16)).toBe(6);
        expect(gridColumns(328, 100, 12)).toBe(3);
        expect(gridColumns(50, 140, 16)).toBe(1);
    });
});

describe('BrowseState', () => {
    it('keeps the last lists left, the newest first, and a sort per scope', () => {
        localStorage.clear();
        const state = new BrowseState();
        for (let i = 0; i < 8; i++)
            state.keep({ key: `k${i}`, items: [i], next: null, offset: 0 });
        expect(state.restore('k7')?.items).toEqual([7]);
        expect(state.restore('k2')?.items).toEqual([2]);
        expect(state.restore('k1')).toBeNull();

        expect(state.sort('series', ['number', 'title'])).toBe('number');
        state.setSort('title', 'series');
        expect(state.sort('series', ['number', 'title'])).toBe('title');
        expect(state.sort()).toBe('title');
        state.setSort('number');
        expect(state.sort(), 'not one of the library sorts').toBe('title');
    });
});

describe('BrowsePage', () => {
    async function render(api: ApiMock, preferences: Record<string, unknown> = {}) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        TestBed.inject(SessionService).set(sessionInfo(['library.browse'], { preferences }));
        const fixture = TestBed.createComponent(BrowsePage);
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const stable = async () => { await settle(); await fixture.whenStable(); };
        return { fixture, element: fixture.nativeElement as HTMLElement, stable };
    }

    const libraries = (api: ApiMock, bookCount = 120) =>
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount }]);

    it('shows the library\'s books as covers: title, authors, series, the count', async () => {
        const api = mockApi();
        libraries(api);
        api.books.mockResolvedValue({ items: [book(1, { series: 'Rincewind', seriesNumber: 1 }), book(2, { title: null })], next: null });
        const { element } = await render(api, { 'ui.pageSize': 30 });

        expect(element.querySelector('h1')?.textContent).toBe('Discworld');
        expect(element.querySelector('.browse-count')?.textContent).toBe('120 books');
        expect(api.books).toHaveBeenCalledWith({ library: 5, sort: 'title', tag: null, after: null, limit: 30 });
        const books = [...element.querySelectorAll('.books.grid .book')];
        expect(books.map(b => b.querySelector('.book-title')?.textContent)).toEqual(['Book 1', 'book-2.fb2']);
        expect(books[0].querySelector('.book-series')?.textContent).toBe('Rincewind #1');
        expect(books[0].querySelector('img')?.getAttribute('src')).toBe('api/books/1/cover/m');
        expect(books[0].getAttribute('aria-posinset')).toBe('1');
        expect(books[0].getAttribute('aria-setsize')).toBe('120');
    });

    it('sorts and switches to the list, remembering both for this browser', async () => {
        const api = mockApi();
        libraries(api);
        api.books.mockResolvedValue(page(1, 3, null));
        const { element, stable } = await render(api);

        const select = element.querySelector<HTMLSelectElement>('.browse-sort select')!;
        select.value = 'added';
        select.dispatchEvent(new Event('change'));
        await stable();
        expect(api.books).toHaveBeenLastCalledWith(expect.objectContaining({ sort: 'added', after: null }));
        expect(localStorage.getItem('librarion.browse.sort')).toBe('added');

        const list = element.querySelector<HTMLButtonElement>('[aria-label="List"]')!;
        list.click();
        await stable();
        expect(list.getAttribute('aria-pressed')).toBe('true');
        expect(element.querySelector('.books.list')).not.toBeNull();
        expect(element.querySelector('.book img')?.getAttribute('src')).toBe('api/books/1/cover/s');
        expect(element.querySelector('.book-formats .badge')?.textContent).toBe('fb2');
        expect(localStorage.getItem('librarion.browse.view')).toBe('list');
    });

    it('asks for the next page by key when the end of what is loaded comes near', async () => {
        const api = mockApi();
        libraries(api);
        api.books.mockResolvedValueOnce(page(1, 4, 'k4'));
        api.books.mockResolvedValueOnce(page(5, 2, null));
        const { element } = await render(api);

        expect(api.books).toHaveBeenCalledTimes(2);
        expect(api.books).toHaveBeenLastCalledWith(expect.objectContaining({ after: 'k4' }));
        expect(element.querySelectorAll('.book')).toHaveLength(6);
    });

    it('shows the list as it was when coming back, without asking again', async () => {
        const api = mockApi();
        libraries(api);
        api.books.mockResolvedValue(page(1, 3, null));
        const first = await render(api);
        first.fixture.destroy();
        const kept = TestBed.inject(BrowseState).restore('books|5||title');
        expect(kept?.items).toHaveLength(3);

        const fixture = TestBed.createComponent(BrowsePage);
        await fixture.whenStable();
        await settle();
        expect(api.books).toHaveBeenCalledTimes(1);
        expect((fixture.nativeElement as HTMLElement).querySelectorAll('.book')).toHaveLength(3);
    });

    it('says when the library is empty, or when there is no library', async () => {
        const api = mockApi();
        libraries(api, 0);
        api.books.mockResolvedValue({ items: [], next: null });
        const { element } = await render(api);
        expect(element.querySelector('.empty')?.textContent).toContain('No books in this library yet.');

        const none = mockApi();
        none.libraries.mockResolvedValue([]);
        const other = await render(none);
        expect(other.element.querySelector('h1')?.textContent).toBe('No library');
        expect(none.books).not.toHaveBeenCalled();
    });
});
