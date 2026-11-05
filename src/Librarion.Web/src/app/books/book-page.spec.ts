import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookDetail, BookListItem, FieldInfo, LibraryInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { BookPage } from './book-page';

const FIELDS: FieldInfo[] = [
    { id: 3, key: 'author', name: 'author', builtIn: true },
    { id: 7, key: 'series', name: 'series', builtIn: true },
    { id: 9, key: 'category', name: 'category', builtIn: true },
    { id: 11, key: 'language', name: 'language', builtIn: true },
];

const detail = (overrides: Partial<BookDetail> = {}): BookDetail => ({
    id: 42, libraryId: 5, title: 'Guards! Guards!', fileName: null, description: 'The watch.\n\nAnd a dragon.', rating: 9,
    added: '2026-11-01T10:00:00Z', coverSource: 'book',
    tags: [
        { id: 21, field: 'author', name: 'Terry Pratchett', number: null },
        { id: 22, field: 'author', name: 'Neil Gaiman', number: null },
        { id: 30, field: 'series', name: 'Discworld', number: 8 },
        { id: 40, field: 'category', name: 'Fantasy', number: null },
        { id: 41, field: 'category', name: 'Humour', number: null },
        { id: 50, field: 'language', name: 'en', number: null },
        { id: 60, field: null, name: 'Favourites', number: null },
    ],
    files: [
        { id: 7, format: 'epub', size: 2_400_000, name: 'Guards.epub', inArchive: true, archive: 'fb2-000.zip', missing: false },
        { id: 8, format: 'fb2', size: 900, name: 'Guards.fb2', inArchive: false, archive: null, missing: true },
    ],
    identifiers: [{ key: 'isbn', value: '9780552134637' }, { key: 'goodreads', value: '64216' }],
    mergedIntoId: null, properties: [{ key: 'Shelf', value: 'Top' }], canDownload: true, ...overrides,
});

const listed = (id: number, title: string): BookListItem => ({
    id, libraryId: 5, title, fileName: null, authors: ['Terry Pratchett'], series: null, seriesNumber: null, formats: ['fb2'], coverSource: 'generated',
});

describe('The book page', () => {
    /** The API with no duplicates to suggest, unless a test says otherwise. */
    function bookApi(): ApiMock {
        const api = mockApi();
        api.bookDuplicates.mockResolvedValue([]);
        return api;
    }

    async function open(url: string, api: ApiMock, libraries: LibraryInfo[] = [{ id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 }]) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        api.libraries.mockResolvedValue(libraries);
        api.fields.mockResolvedValue(FIELDS);
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        const router = TestBed.inject(Router);
        router.resetConfig([{ path: 'books/:id', component: BookPage }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl(url);
        for (let i = 0; i < 6; i++) {
            await settle();
            harness.detectChanges();
            await harness.fixture.whenStable();
        }
        return { harness, router, element: harness.routeNativeElement as HTMLElement };
    }

    it('shows the title, authors and series as links, the other fields as chips and the plain tags last', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail());
        const { element } = await open('/books/42', api);

        expect(api.book).toHaveBeenCalledWith(42);
        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Guards! Guards!');
        expect(element.querySelector('h1')?.classList).not.toContain('file-title');
        expect(element.querySelector('.book-page-cover img')?.getAttribute('src')).toBe('api/books/42/cover/l');
        const authors = [...element.querySelectorAll('.book-by a')];
        expect(authors.map(a => [a.textContent, a.getAttribute('href')])).toEqual([['Terry Pratchett', '/author/21/Terry%20Pratchett'], ['Neil Gaiman', '/author/22/Neil%20Gaiman']]);
        expect(element.querySelector('.book-in-series a')?.textContent).toBe('Discworld #8');
        expect(element.querySelector('.book-rating')?.textContent?.trim()).toBe('4.5 of 5');
        const fields = [...element.querySelectorAll('.book-field')].map(f => [
            f.querySelector('dt')?.textContent, [...f.querySelectorAll('dd .chip')].map(c => c.textContent),
        ]);
        expect(fields).toEqual([['Categories', ['Fantasy', 'Humour']], ['Language', ['English']], ['Tag', ['Favourites']]]);
        expect(element.querySelector('.book-description')?.textContent).toBe('The watch.\n\nAnd a dragon.');
        expect(element.querySelector('.tag-kind'), 'one library: no need to name it').toBeNull();
    });

    it('lists the files with their size and archive, downloads those still there, and gives the first in the head', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail());
        const { element } = await open('/books/42', api);

        const head = element.querySelector<HTMLAnchorElement>('.book-actions a')!;
        expect(head.textContent?.trim()).toBe('Download EPUB');
        expect(head.getAttribute('href')).toBe('api/books/42/files/7');
        expect(head.hasAttribute('download')).toBe(true);
        const files = [...element.querySelectorAll('.book-file')];
        expect(files[0].querySelector('.book-file-where')?.textContent).toMatch(/2\.4 MB\s*· in fb2-000\.zip/);
        expect(files[0].querySelector('a[download]')?.getAttribute('href')).toBe('api/books/42/files/7');
        expect(files[1].classList).toContain('missing');
        expect(files[1].querySelector('a[download]'), 'a missing file has no download').toBeNull();
        expect(files[1].textContent).toContain('missing since the last scan');
        const details = [...element.querySelectorAll('.book-details > div')].map(d => [d.querySelector('dt')?.textContent, d.querySelector('dd')?.textContent]);
        expect(details.slice(1)).toEqual([['ISBN', '9780552134637'], ['goodreads', '64216'], ['Shelf', 'Top']]);
    });

    it('offers no download without the right, and says so', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail({ canDownload: false }));
        const { element } = await open('/books/42', api);

        expect(element.querySelector('a[download]')).toBeNull();
        expect(element.querySelector('.book-note')?.textContent).toContain('You may not download');
    });

    it('marks a title taken from the file name and names the library when there are several', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail({ title: null, fileName: '123456', tags: [], rating: null, description: null }));
        const { element } = await open('/books/42', api, [
            { id: 5, tenantId: 1, name: 'Discworld', created: '' }, { id: 6, tenantId: 1, name: 'Comics', created: '' },
        ]);

        const title = element.querySelector('h1')!;
        expect(title.textContent?.trim()).toBe('123456');
        expect(title.classList).toContain('file-title');
        expect(title.getAttribute('title')).toContain('file name');
        expect(element.querySelector('.tag-kind')?.textContent?.trim()).toBe('Discworld');
        expect(element.querySelector('.book-by')).toBeNull();
        expect(element.querySelector('.book-description')).toBeNull();
    });

    it('lists possible duplicates with why, each opening its page', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail());
        api.bookDuplicates.mockResolvedValue([
            { book: listed(43, 'Guards! Guards! (scan)'), reason: 'identifier', scheme: 'isbn' },
            { book: listed(44, 'Guards Guards'), reason: 'work', scheme: null },
        ]);
        const { element } = await open('/books/42', api);

        expect(api.bookDuplicates).toHaveBeenCalledWith(42);
        const rows = [...element.querySelectorAll('.book-duplicate')];
        expect(rows.map(r => r.querySelector('.book-reason')?.textContent)).toEqual(['Same ISBN', 'Same title and author']);
        expect(rows[0].getAttribute('href')).toBe('/books/43');
        expect(rows[0].querySelector('.book-title')?.textContent).toBe('Guards! Guards! (scan)');
    });

    it('shows the book without duplicates when they fail, and none when there are none', async () => {
        const api = bookApi();
        api.book.mockResolvedValue(detail());
        api.bookDuplicates.mockRejectedValue(new ApiError(500, 'boom'));
        const { element } = await open('/books/42', api);

        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Guards! Guards!');
        expect(element.querySelector('.book-duplicates')).toBeNull();
        expect(element.querySelector('[role="alert"]')).toBeNull();
    });

    it('goes to the book a merged one lives in, replacing the address', async () => {
        const api = bookApi();
        api.book.mockImplementation(async (id: number) => id === 42 ? detail({ mergedIntoId: 99 }) : detail({ id: 99, title: 'Kept' }));
        const { router, element } = await open('/books/42', api);

        expect(router.url).toBe('/books/99');
        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Kept');
    });

    it('says a book it can\'t see is not found, and offers to try again after other errors', async () => {
        const api = bookApi();
        api.book.mockRejectedValue(new ApiError(404, 'missing'));
        const { element } = await open('/books/42', api);
        expect(element.querySelector('h1')?.textContent).toBe('Not found');

        const failing = bookApi();
        failing.book.mockRejectedValueOnce(new ApiError(500, 'boom'));
        failing.book.mockResolvedValue(detail());
        const again = await open('/books/42', failing);
        expect(again.element.querySelector('[role="alert"]')).not.toBeNull();
        again.element.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
        for (let i = 0; i < 3; i++) {
            await settle();
            again.harness.detectChanges();
        }
        expect(again.element.querySelector('h1')?.textContent?.trim()).toBe('Guards! Guards!');
    });
});
