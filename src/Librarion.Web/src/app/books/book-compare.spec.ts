import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookDetail, BookDuplicate } from '../core/models';
import { SessionService } from '../core/session.service';
import { BookPage } from './book-page';

const detail = (id: number, overrides: Partial<BookDetail> = {}): BookDetail => ({
    id, libraryId: 5, title: `Book ${id}`, fileName: `${id}.fb2`, description: null, rating: null, added: '2026-11-01T10:00:00Z', coverSource: 'book',
    tags: [{ id: 1, field: 'author', name: 'Frank Herbert', number: null }], files: [{ id: id * 10, format: 'fb2', size: 1000, name: `${id}.fb2`, inArchive: false, archive: null, missing: false }],
    identifiers: [], mergedIntoId: null, properties: [], canDownload: true, canTag: false, canEdit: false, canMerge: true, coverVersion: 'v1', ...overrides,
});

const duplicate = (id: number): BookDuplicate => ({
    book: { id, libraryId: 5, title: `Book ${id}`, fileName: null, authors: ['Frank Herbert'], series: null, seriesNumber: null, formats: ['fb2'], coverSource: 'book' },
    reason: 'work', scheme: null,
});

describe('Compare and merge from a book\'s page', () => {
    async function open(api: ApiMock, books: Record<number, BookDetail>) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        api.libraries.mockResolvedValue([]);
        api.fields.mockResolvedValue([]);
        api.book.mockImplementation((id: number) => Promise.resolve(books[id]));
        api.bookDuplicates.mockResolvedValue([duplicate(43)]);
        TestBed.inject(SessionService).set(sessionInfo(['library.browse', 'library.merge']));
        TestBed.inject(Router).resetConfig([{ path: 'books/:id', component: BookPage }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl('/books/42');
        await settle();
        harness.detectChanges();
        const element = harness.routeNativeElement!;
        const stable = async () => { await settle(); harness.detectChanges(); await harness.fixture.whenStable(); };
        return { element, stable, harness };
    }

    const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;

    it('keeps the book with most metadata, marks what differs, and merges with the values chosen', async () => {
        const api = mockApi();
        const books = {
            42: detail(42, { title: 'Dune' }),
            43: detail(43, { title: 'DUNE: complete', description: 'The long one.', identifiers: [{ key: 'isbn', value: '978' }] }),
        };
        api.mergeBooks.mockResolvedValue(detail(43, { title: 'Dune' }));
        const { element, stable } = await open(api, books);

        button(element, 'Compare and merge').click();
        await stable();
        const cards = [...element.querySelectorAll<HTMLElement>('.compare-book')];
        expect(cards).toHaveLength(2);
        expect(cards[1].classList).toContain('kept');
        expect(cards[1].querySelector<HTMLInputElement>('input[name="compare-keep"]')!.checked).toBe(true);
        expect(cards[0].querySelector('.compare-row.differs .compare-title')?.textContent).toBe('Dune');

        // The title from the other book, then merged.
        const titles = [...element.querySelectorAll<HTMLInputElement>('input[name="compare-title"]')];
        titles[0].click();
        await stable();
        button(element, 'Merge 2 books').click();
        await stable();

        expect(api.mergeBooks).toHaveBeenCalledWith({ targetId: 43, sourceIds: [42], titleFrom: 42, descriptionFrom: null, ratingFrom: null, coverFrom: null });
        expect(TestBed.inject(Router).url).toBe('/books/43');
    });

    it('leaves out a book of another library and shows a refusal', async () => {
        const api = mockApi();
        const books = { 42: detail(42, { tags: [], files: [] }), 43: detail(43, { libraryId: 9 }) };
        api.mergeBooks.mockRejectedValue(new ApiError(400, 'x', { sourceIds: ['merge.books'] }));
        const { element, stable } = await open(api, books);

        button(element, 'Compare and merge').click();
        await stable();
        const other = [...element.querySelectorAll<HTMLElement>('.compare-book')].find(c => !c.classList.contains('kept'))!;
        expect(other.textContent).toContain('In another library');
        expect(button(element, 'Merge').disabled).toBe(true);
    });

    it('says a candidate is not a duplicate: it leaves the list', async () => {
        const api = mockApi();
        api.notDuplicates.mockResolvedValue(undefined);
        const { element, stable } = await open(api, { 42: detail(42), 43: detail(43) });

        button(element, 'Not a duplicate').click();
        await stable();
        expect(api.notDuplicates).toHaveBeenCalledWith([42, 43]);
        expect(element.querySelector('.book-duplicates')).toBeNull();
    });
});
