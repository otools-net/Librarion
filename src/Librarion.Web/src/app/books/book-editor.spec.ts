import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookDetail, FieldInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { BookPage } from './book-page';

const FIELDS: FieldInfo[] = [
    { id: 3, key: 'author', name: 'author', builtIn: true },
    { id: 7, key: 'series', name: 'series', builtIn: true },
    { id: 9, key: 'category', name: 'category', builtIn: true },
    { id: 12, key: 'publisher', name: 'publisher', builtIn: true },
];

const detail = (overrides: Partial<BookDetail> = {}): BookDetail => ({
    id: 42, libraryId: 5, title: 'Good Omens', fileName: 'omens.epub', description: 'The end.', rating: 8, added: '2026-11-01T10:00:00Z', coverSource: 'book',
    tags: [
        { id: 21, field: 'author', name: 'Terry Pratchett', number: null },
        { id: 22, field: 'author', name: 'Neil Gaiman', number: null },
        { id: 30, field: 'series', name: 'Omens', number: 1 },
        { id: 40, field: 'category', name: 'Fantasy', number: null },
        { id: 60, field: null, name: 'Favourites', number: null },
    ],
    files: [], identifiers: [], mergedIntoId: null, properties: [], canDownload: false, canTag: true, canEdit: true, ...overrides,
});

describe('The book editor', () => {
    async function open(api: ApiMock, book = detail()) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        api.libraries.mockResolvedValue([]);
        api.fields.mockResolvedValue(FIELDS);
        api.bookDuplicates.mockResolvedValue([]);
        api.book.mockResolvedValue(book);
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        TestBed.inject(Router).resetConfig([{ path: 'books/:id', component: BookPage }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl('/books/42');
        const stable = async (ms = 0) => {
            for (let i = 0; i < 4; i++) {
                await settle(ms);
                harness.detectChanges();
                await harness.fixture.whenStable();
            }
        };
        await stable();
        const element = harness.routeNativeElement as HTMLElement;
        const button = (text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text);
        const labelled = (label: string) => element.querySelector<HTMLButtonElement>(`[aria-label="${label}"]`)!;
        return { harness, element, stable, button, labelled };
    }

    async function type(box: HTMLInputElement, text: string, stable: (ms?: number) => Promise<void>) {
        box.focus();
        box.value = text;
        box.dispatchEvent(new Event('input'));
        await stable(200);
    }

    it('is offered only to those who may tag or edit the book', async () => {
        const reader = await open(mockApi(), detail({ canTag: false, canEdit: false }));
        expect(reader.button('Edit')).toBeUndefined();

        const tagger = await open(mockApi(), detail({ canEdit: false }));
        tagger.button('Edit')!.click();
        await tagger.stable();
        const legends = [...tagger.element.querySelectorAll('.book-editor legend')].map(l => l.textContent?.trim());
        expect(legends).toEqual(['Tags']);
        expect(tagger.element.querySelector('#book-title-input')).toBeNull();
    });

    it('sends only what changed: the authors reordered, a category taken off, a series number, a new tag by name', async () => {
        const api = mockApi();
        api.editBook.mockResolvedValue(detail({ title: 'Saved', coverVersion: 'b2' }));
        api.pickTags.mockResolvedValue({ items: [], exact: false });
        const { element, stable, button, labelled } = await open(api);
        button('Edit')!.click();
        await stable();

        labelled('Move Neil Gaiman up').click();
        labelled('Remove Fantasy').click();
        const number = labelled('Number of the book in Omens') as unknown as HTMLInputElement;
        number.value = '2.5';
        number.dispatchEvent(new Event('input'));
        await type(element.querySelector<HTMLInputElement>('#book-add-tag')!, 'To read', stable);
        [...element.querySelectorAll<HTMLElement>('#book-add-tag-list [role="option"]')].find(o => o.textContent?.includes('Create'))!.click();
        await stable();
        expect([...element.querySelectorAll('.book-editor .tag-chips .chip span')].map(c => c.textContent)).toEqual(['Favourites', 'To read']);

        element.querySelector<HTMLButtonElement>('button[form="book-form"]')!.click();
        await stable();

        expect(api.editBook).toHaveBeenCalledWith(42, {
            fields: [
                { field: 'author', tags: [{ id: 22, number: null }, { id: 21, number: null }] },
                { field: 'series', tags: [{ id: 30, number: 2.5 }] },
                { field: 'category', tags: [] },
                { field: null, tags: [{ id: 60, number: null }, { name: 'To read', number: null }] },
            ],
        });
        expect(element.querySelector('lib-book-editor')).toBeNull();
        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Saved');
        expect(element.querySelector('.book-page-cover img')?.getAttribute('src')).toBe('api/books/42/cover/l?v=b2');
    });

    it('sends the columns when one changed, a field added from the list, and shows the server\'s refusals', async () => {
        const api = mockApi();
        api.editBook.mockRejectedValueOnce(new ApiError(400, 'x', { rating: ['rating.invalid'], tags: ['tags.tooMany'] }));
        api.pickTags.mockResolvedValue({ items: [{ id: 70, name: 'Gollancz', color: null, field: 'publisher', shorthand: null, alias: null, parent: null, books: 9 }], exact: false });
        const { element, stable, button } = await open(api);
        button('Edit')!.click();
        await stable();

        const title = element.querySelector<HTMLInputElement>('#book-title-input')!;
        title.value = 'Good Omens (1990)';
        title.dispatchEvent(new Event('input'));
        const add = element.querySelector<HTMLSelectElement>('#book-add-field')!;
        add.value = 'publisher';
        add.dispatchEvent(new Event('change'));
        await stable();
        await type(element.querySelector<HTMLInputElement>('#book-add-publisher')!, 'goll', stable);
        element.querySelector<HTMLElement>('#book-add-publisher-list [role="option"]')!.click();
        await stable();
        element.querySelector<HTMLButtonElement>('button[form="book-form"]')!.click();
        await stable();

        expect(api.editBook).toHaveBeenCalledWith(42, {
            columns: { title: 'Good Omens (1990)', description: 'The end.', rating: 8 },
            fields: [{ field: 'publisher', tags: [{ id: 70, number: null }] }],
        });
        expect(element.querySelector('#book-rating-input')?.parentElement?.querySelector('.field-error')?.textContent).toBe('The rating goes from 0 to 5 stars.');
        expect(element.querySelector('.book-editor [role="alert"]')?.textContent).toContain('100 values at most');
        expect(element.querySelector('lib-book-editor')).not.toBeNull();
    });

    it('closes without asking the server when nothing changed', async () => {
        const api = mockApi();
        const { element, stable, button } = await open(api);
        button('Edit')!.click();
        await stable();
        element.querySelector<HTMLButtonElement>('button[form="book-form"]')!.click();
        await stable();
        expect(api.editBook).not.toHaveBeenCalled();
        expect(element.querySelector('lib-book-editor')).toBeNull();
    });
});
