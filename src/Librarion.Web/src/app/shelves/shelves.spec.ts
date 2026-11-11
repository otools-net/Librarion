import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookListItem, ShelfInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { BookPage } from '../books/book-page';
import { BrowsePage } from '../browse/browse-page';
import { inListOrder, moveIndex, movedLocally } from '../browse/shelf-order';
import { ShelfPage } from './shelf-page';
import { ShelvesPage } from './shelves-page';

const book = (id: number): BookListItem => ({
    id, libraryId: 5, title: `Book ${id}`, fileName: null, authors: ['Ann Writer'], series: null, seriesNumber: null, formats: ['fb2'], coverSource: 'generated',
});
const shelf = (id: number, name: string, bookCount = 0): ShelfInfo => ({ id, name, bookCount, created: '', updated: '' });
const ids = (items: { id: number }[]) => items.map(i => i.id);

describe('A shelf\'s order', () => {
    const items = [1, 2, 3, 4, 5].map(id => ({ id }));

    it('moves books before another or to the end; to the end they leave a list that isn\'t whole yet', () => {
        expect(ids(movedLocally(items, [4, 2], 1, true))).toEqual([2, 4, 1, 3, 5]);
        expect(ids(movedLocally(items, [1], null, true))).toEqual([2, 3, 4, 5, 1]);
        expect(ids(movedLocally(items, [1], null, false))).toEqual([2, 3, 4, 5]);
        expect(inListOrder(items, new Set([5, 1, 3]))).toEqual([1, 3, 5]);
    });

    it('finds the place among the others for each move, none when already there', () => {
        expect(moveIndex(items, [3], 'up')).toBe(1);
        expect(moveIndex(items, [3], 'down')).toBe(3);
        expect(moveIndex(items, [3], 'start')).toBe(0);
        expect(moveIndex(items, [1], 'up')).toBeNull();
        expect(moveIndex(items, [1, 2], 'start')).toBeNull();
        // Scattered books are gathered even where the first of them stays.
        expect(moveIndex(items, [1, 3], 'start')).toBe(0);
        // The last loaded goes on past the next one (on the next page, when there is one).
        expect(moveIndex(items, [5], 'down')).toBe(5);
    });
});

describe('Shelves', () => {
    async function open(url: string, api: ApiMock, rights = ['library.browse', 'account.shelves']) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 4 }]);
        api.books.mockResolvedValue({ items: [book(1), book(2), book(3), book(4)], next: null });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        TestBed.inject(Router).resetConfig([
            { path: '', component: BrowsePage },
            { path: 'shelves', component: ShelvesPage },
            { path: 'shelves/:id', component: ShelfPage },
            { path: 'shelves/:id/:name', component: ShelfPage },
            { path: 'books/:id', component: BookPage },
        ]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl(url);
        const stable = async () => {
            for (let i = 0; i < 3; i++) {
                await settle();
                harness.detectChanges();
                await harness.fixture.whenStable();
            }
        };
        await stable();
        const element = () => harness.fixture.nativeElement as HTMLElement;
        const button = (text: string) => [...element().querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;
        return { harness, element, stable, button, router: TestBed.inject(Router) };
    }

    it('lists the account\'s shelves, makes one and opens it', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([shelf(1, 'To read', 3)]);
        api.createShelf.mockResolvedValue(shelf(2, 'Favourites'));
        api.shelf.mockResolvedValue(shelf(2, 'Favourites'));
        api.shelfBooks.mockResolvedValue({ items: [], next: null });
        const { element, stable, router } = await open('/shelves', api);

        const row = element().querySelector<HTMLAnchorElement>('.shelf-row')!;
        expect(row.getAttribute('href')).toBe('/shelves/1/To%20read');
        expect(row.querySelector('.shelf-row-count')?.textContent).toBe('3 books');

        const input = element().querySelector<HTMLInputElement>('.shelf-new input')!;
        input.value = ' Favourites ';
        input.dispatchEvent(new Event('input'));
        await stable();
        element().querySelector<HTMLFormElement>('.shelf-new')!.dispatchEvent(new Event('submit', { cancelable: true }));
        await stable();
        expect(api.createShelf).toHaveBeenCalledWith('Favourites');
        expect(router.url).toBe('/shelves/2/Favourites');
        expect(element().querySelector('h1')?.textContent).toBe('Favourites');
        expect(element().querySelector('.empty')?.textContent).toContain('This shelf has no books you can see.');
    });

    it('shows a shelf\'s books in its own order, each with a grip; the keys move a book, the address takes the name', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([shelf(3, 'Mine', 4)]);
        api.shelf.mockResolvedValue(shelf(3, 'Mine', 4));
        api.shelfBooks.mockResolvedValue({ items: [book(1), book(2), book(3), book(4)], next: null });
        api.moveOnShelf.mockResolvedValue(undefined);
        const { element, stable, router } = await open('/shelves/3', api);

        expect(router.url).toBe('/shelves/3/Mine');
        expect(api.shelfBooks).toHaveBeenCalledWith(3, { sort: 'shelf', after: null, limit: 50 });
        expect([...element().querySelectorAll('select option')].map(o => o.textContent)).toEqual(['Shelf order', 'Title', 'Author', 'Date added']);
        const order = () => [...element().querySelectorAll('li.book')].map(li => Number(li.getAttribute('data-book')));
        const grip = (id: number) => element().querySelector<HTMLButtonElement>(`li[data-book="${id}"] .book-move`)!;
        expect(grip(2).getAttribute('aria-label')).toBe('Move Book 2');

        grip(3).dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }));
        await stable();
        expect(api.moveOnShelf).toHaveBeenLastCalledWith(3, [3], 2);
        expect(order()).toEqual([1, 3, 2, 4]);
        expect(element().querySelector('lib-book-list > [role="status"]')?.textContent).toBe('Moved to place 2.');

        grip(1).dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true }));
        await stable();
        expect(api.moveOnShelf).toHaveBeenLastCalledWith(3, [1], null);
        expect(order()).toEqual([3, 2, 4, 1]);

        // A refusal puts the list back.
        api.moveOnShelf.mockRejectedValueOnce(new ApiError(409, 'x', undefined, 'shelf.order'));
        grip(4).dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true, cancelable: true }));
        await stable();
        expect(api.moveOnShelf).toHaveBeenLastCalledWith(3, [4], 3);
        expect(order()).toEqual([3, 2, 4, 1]);

        // Sorted by title: no grips.
        const select = element().querySelector<HTMLSelectElement>('select')!;
        select.value = 'title';
        select.dispatchEvent(new Event('change'));
        await stable();
        expect(api.shelfBooks).toHaveBeenLastCalledWith(3, { sort: 'title', after: null, limit: 50 });
        expect(element().querySelector('.book-move')).toBeNull();
    });

    it('moves a selection with the Move dialog, and takes it off the shelf', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([shelf(3, 'Mine', 4)]);
        api.shelf.mockResolvedValue(shelf(3, 'Mine', 4));
        api.shelfBooks.mockResolvedValue({ items: [book(1), book(2), book(3), book(4)], next: null });
        api.moveOnShelf.mockResolvedValue(undefined);
        api.removeFromShelf.mockResolvedValue(shelf(3, 'Mine', 2));
        const { element, stable, button } = await open('/shelves/3/Mine', api);
        const order = () => [...element().querySelectorAll('li.book')].map(li => Number(li.getAttribute('data-book')));

        element().querySelector<HTMLButtonElement>('li[data-book="4"] .book-select')!.click();
        element().querySelector<HTMLButtonElement>('li[data-book="2"] .book-select')!.click();
        await stable();
        // Without tagging rights the shelf is the bar's main action.
        expect(button('Tags')).toBeUndefined();
        element().querySelector<HTMLButtonElement>('li[data-book="4"] .book-move')!.click();
        await stable();
        expect(element().querySelector('lib-shelf-move h2')?.textContent).toBe('Move 2 books');
        expect(element().querySelector('.shelf-move-place')?.textContent).toBe('Place on the shelf: 2');
        button('To the start').click();
        await stable();
        expect(api.moveOnShelf).toHaveBeenLastCalledWith(3, [2, 4], 1);
        expect(order()).toEqual([2, 4, 1, 3]);
        expect(element().querySelector('.shelf-move-place')?.textContent).toBe('Place on the shelf: 1');
        button('Done').click();
        await stable();

        button('Remove from shelf').click();
        await stable();
        expect(api.removeFromShelf).toHaveBeenCalledWith(3, [2, 4]);
        expect(order()).toEqual([1, 3]);
        expect(element().querySelector('.browse-count')?.textContent).toBe('2 books');
    });

    it('renames a shelf and deletes it, asking first', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([shelf(3, 'Mine', 4)]);
        api.shelf.mockResolvedValue(shelf(3, 'Mine', 4));
        api.shelfBooks.mockResolvedValue({ items: [], next: null });
        api.renameShelf.mockResolvedValueOnce(shelf(3, 'Ours', 4));
        api.deleteShelf.mockResolvedValue(undefined);
        const { element, stable, button, router } = await open('/shelves/3/Mine', api);

        button('Rename').click();
        await stable();
        const input = element().querySelector<HTMLInputElement>('#shelf-rename-form input')!;
        expect(input.value).toBe('Mine');
        input.value = 'Ours';
        input.dispatchEvent(new Event('input'));
        await stable();
        element().querySelector<HTMLFormElement>('#shelf-rename-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
        await stable();
        expect(api.renameShelf).toHaveBeenCalledWith(3, 'Ours');
        expect(router.url).toBe('/shelves/3/Ours');
        expect(element().querySelector('h1')?.textContent).toBe('Ours');

        const ask = vi.spyOn(TestBed.inject(ConfirmService), 'ask').mockResolvedValue(true);
        button('Delete').click();
        await stable();
        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ title: 'Delete Ours?', danger: true }));
        expect(api.deleteShelf).toHaveBeenCalledWith(3);
        expect(router.url).toBe('/shelves');
    });

    it('a missing shelf says so', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([]);
        api.shelf.mockRejectedValue(new ApiError(404, 'x'));
        const { element } = await open('/shelves/9', api);
        expect(element().querySelector('h1')?.textContent).toBe('No such shelf');
    });

    it('puts a selection of the library on a shelf, and makes a new one for it', async () => {
        const api = mockApi();
        api.shelves.mockResolvedValue([shelf(3, 'Mine', 1)]);
        api.addToShelf.mockResolvedValueOnce({ shelf: shelf(3, 'Mine', 2), added: 1, already: 1 })
            .mockResolvedValueOnce({ shelf: shelf(4, 'New', 2), added: 2, already: 0 });
        api.createShelf.mockResolvedValue(shelf(4, 'New'));
        const { element, stable, button } = await open('/', api, ['library.browse', 'library.tag', 'account.shelves']);

        element().querySelector<HTMLButtonElement>('li[data-book="1"] .book-select')!.click();
        element().querySelector<HTMLButtonElement>('li[data-book="2"] .book-select')!.click();
        await stable();
        expect(element().querySelector('.book-move')).toBeNull();
        expect(button('Remove from shelf')).toBeUndefined();
        button('Add to shelf').click();
        await stable();
        expect(element().querySelector('lib-shelf-picker h2')?.textContent).toBe('Add 2 books to shelves');
        expect(api.bookShelves).not.toHaveBeenCalled();
        const box = element().querySelector<HTMLInputElement>('.shelf-pick-rows input')!;
        expect(box.checked).toBe(false);
        box.click();
        await stable();
        expect(api.addToShelf).toHaveBeenCalledWith(3, [1, 2]);
        expect(TestBed.inject(ToastService).toasts().map(t => t.message)).toEqual(['Put 1 on Mine (1 were there already).']);

        const name = element().querySelector<HTMLInputElement>('lib-shelf-picker .shelf-new input')!;
        name.value = 'New';
        name.dispatchEvent(new Event('input'));
        await stable();
        element().querySelector<HTMLFormElement>('lib-shelf-picker .shelf-new')!.dispatchEvent(new Event('submit', { cancelable: true }));
        await stable();
        expect(api.createShelf).toHaveBeenCalledWith('New');
        expect(api.addToShelf).toHaveBeenLastCalledWith(4, [1, 2]);
        expect([...element().querySelectorAll('.shelf-pick-rows li')].map(li => [li.querySelector('.shelf-pick-name')?.textContent, li.querySelector('.field-hint')?.textContent, li.querySelector('input')!.checked]))
            .toEqual([['Mine', '2', true], ['New', '2', true]]);
    });

    it('on a book\'s page, ticks the shelves holding it and takes it off one', async () => {
        const api = mockApi();
        api.book.mockResolvedValue({
            id: 7, libraryId: 5, title: 'Mort', fileName: null, description: null, rating: null, added: '2026-11-01T10:00:00Z', coverSource: 'generated',
            tags: [], files: [], identifiers: [], properties: [], canEdit: false, canTag: false, canMerge: false,
        } as never);
        api.bookDuplicates.mockResolvedValue([]);
        api.shelves.mockResolvedValue([shelf(3, 'Mine', 2), shelf(4, 'Later', 0)]);
        api.bookShelves.mockResolvedValue([3]);
        api.removeFromShelf.mockResolvedValue(shelf(3, 'Mine', 1));
        const { element, stable, button } = await open('/books/7', api);

        button('Add to shelf').click();
        await stable();
        expect(api.bookShelves).toHaveBeenCalledWith(7);
        const boxes = [...element().querySelectorAll<HTMLInputElement>('.shelf-pick-rows input')];
        expect(boxes.map(b => b.checked)).toEqual([true, false]);
        boxes[0].click();
        await stable();
        expect(api.removeFromShelf).toHaveBeenCalledWith(3, [7]);
        expect(boxes[0].checked).toBe(false);
    });

    it('is not offered without the right', async () => {
        const api = mockApi();
        const { element } = await open('/', api, ['library.browse']);
        expect(element().querySelector('.book-select')).toBeNull();
    });
});
