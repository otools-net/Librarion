import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookListItem } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { BrowsePage } from './browse-page';

const book = (id: number): BookListItem => ({
    id, libraryId: 5, title: `Book ${id}`, fileName: null, authors: ['Ann Writer'], series: null, seriesNumber: null, formats: ['fb2'], coverSource: 'generated',
});

describe('Selecting and tagging books', () => {
    async function render(api: ApiMock, rights = ['library.browse', 'library.tag']) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 4 }]);
        api.books.mockResolvedValue({ items: [book(1), book(2), book(3), book(4)], next: null });
        api.pickTags.mockResolvedValue({ items: [], exact: false });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const fixture = TestBed.createComponent(BrowsePage);
        const stable = async (ms = 0) => {
            for (let i = 0; i < 3; i++) {
                await settle(ms);
                await fixture.whenStable();
            }
        };
        await stable();
        const element = fixture.nativeElement as HTMLElement;
        const link = (id: number) => element.querySelector<HTMLAnchorElement>(`li[data-book="${id}"] a.book-link`)!;
        const click = async (id: number, init: MouseEventInit = {}) => {
            const event = new MouseEvent('click', { bubbles: true, cancelable: true, ...init });
            link(id).dispatchEvent(event);
            await stable();
            return event;
        };
        const selected = () => [...element.querySelectorAll('li.book.selected')].map(li => Number(li.getAttribute('data-book')));
        const button = (text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;
        return { fixture, element, stable, click, selected, link, button, router: TestBed.inject(Router) };
    }

    it('is not offered without the right to tag', async () => {
        const { element, click, router } = await render(mockApi(), ['library.browse']);
        expect(element.querySelector('.book-select')).toBeNull();
        expect([...element.querySelectorAll('button')].some(b => b.textContent?.includes('Tag all'))).toBe(false);
        const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
        await click(1);
        expect(navigate).toHaveBeenCalled();
    });

    it('selects with the box, Ctrl and Shift; while some are selected a click selects instead of opening; Escape clears', async () => {
        const { element, click, selected, stable, router } = await render(mockApi());
        const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

        element.querySelector<HTMLButtonElement>('li[data-book="2"] .book-select')!.click();
        await stable();
        expect(selected()).toEqual([2]);
        expect(element.querySelector('li[data-book="2"] .book-select')?.getAttribute('aria-checked')).toBe('true');
        expect(element.querySelector('.selection-count')?.textContent).toBe('1 selected');

        const ctrl = await click(4, { ctrlKey: true });
        expect(ctrl.defaultPrevented).toBe(true);
        expect(selected()).toEqual([2, 4]);
        await click(1);
        expect(selected()).toEqual([1, 2, 4]);
        await click(1);
        expect(selected()).toEqual([2, 4]);
        // A range from the book last toggled (1) to this one.
        await click(3, { shiftKey: true });
        expect(selected()).toEqual([1, 2, 3, 4]);
        expect(navigate).not.toHaveBeenCalled();

        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await stable();
        expect(selected()).toEqual([]);
        expect(element.querySelector('.selection-bar')).toBeNull();
    });

    it('selects by a long press on a phone, and the lift opens nothing', async () => {
        const { link, stable, selected, click, router } = await render(mockApi());
        const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
        const press = (type: string, x = 10) => link(3).dispatchEvent(Object.assign(new MouseEvent(type, { bubbles: true, clientX: x, clientY: 10 }), { pointerType: 'touch' }));

        press('pointerdown');
        await stable(600);
        press('pointerup');
        await click(3);
        expect(selected()).toEqual([3]);
        expect(navigate).not.toHaveBeenCalled();

        // A finger that moves scrolls: no selection.
        press('pointerdown');
        press('pointermove', 60);
        await stable(600);
        expect(selected()).toEqual([3]);
    });

    it('shows how many of the selection have each tag and saves only what changed', async () => {
        const api = mockApi();
        api.selectionTags.mockResolvedValue({ books: 2, tags: [{ id: 7, name: 'Horror', color: '#aa0000', books: 1 }, { id: 8, name: 'Shared', color: null, books: 2 }] });
        api.tagSelection.mockResolvedValue({ runId: null, logId: null, links: 3 });
        const { element, stable, button, selected } = await render(api);
        element.querySelector<HTMLButtonElement>('li[data-book="1"] .book-select')!.click();
        element.querySelector<HTMLButtonElement>('li[data-book="2"] .book-select')!.click();
        await stable();
        button('Tags').click();
        await stable();

        expect(api.selectionTags).toHaveBeenCalledWith([1, 2]);
        const boxes = [...element.querySelectorAll<HTMLInputElement>('.selection-tag-rows input[type="checkbox"]')];
        expect(boxes.map(b => [b.checked, b.indeterminate, b.getAttribute('aria-label')])).toEqual([
            [false, true, 'Horror: 1 of 2 have it'], [true, false, 'Shared: all of them have it'],
        ]);
        boxes[0].click();
        boxes[1].click();
        await stable();
        expect(boxes[0].checked).toBe(true);
        expect(boxes[1].getAttribute('aria-label')).toBe('Shared: none of them will have it');

        element.querySelector<HTMLButtonElement>('button[form="selection-tags-form"]')!.click();
        await stable();
        expect(api.tagSelection).toHaveBeenCalledWith([1, 2], { add: [{ id: 7 }], remove: [8] });
        expect(selected()).toEqual([]);
        expect(element.querySelector('lib-selection-tags')).toBeNull();
    });

    it('tags every book of the list: counted first, confirmed with the count, a big change followed in the panel', async () => {
        const api = mockApi();
        api.countScope.mockResolvedValue({ books: 12345 });
        api.tagScope.mockRejectedValueOnce(new ApiError(400, 'x', { add: ['tags.tooMany'] })).mockResolvedValue({ runId: 77, logId: null, links: 12345 });
        const { element, stable, button } = await render(api);
        api.pickTags.mockResolvedValue({ items: [{ id: 9, name: 'Old', color: null, field: null, shorthand: null, alias: null, parent: null, books: 3 }], exact: true });
        const follow = vi.spyOn(TestBed.inject(TaskWatch), 'follow').mockImplementation(() => { });
        button('Tag all').click();
        await stable();

        expect(api.countScope).toHaveBeenCalledWith({ library: 5, tag: null, q: null });
        expect(element.querySelector('.scope-count')?.textContent).toBe('This list has 12,345 books.');
        const apply = element.querySelector<HTMLButtonElement>('button[form="scope-tags-form"]')!;
        expect(apply.textContent?.trim()).toBe('Apply to 12,345 books');
        expect(apply.disabled).toBe(true);

        const remove = element.querySelector<HTMLInputElement>('#scope-tag-remove')!;
        remove.focus();
        remove.value = 'ol';
        remove.dispatchEvent(new Event('input'));
        await stable(200);
        element.querySelector<HTMLElement>('#scope-tag-remove-list [role="option"]')!.click();
        await stable();
        expect(apply.disabled).toBe(false);
        apply.click();
        await stable();
        expect(element.querySelector('lib-scope-tags [role="alert"]')?.textContent).toContain('100 values at most');

        apply.click();
        await stable();
        expect(api.tagScope).toHaveBeenLastCalledWith({ library: 5, tag: null, q: null }, { add: [], remove: [9] });
        expect(follow).toHaveBeenCalledWith(77);
        expect(element.querySelector('lib-scope-tags')).toBeNull();
    });
});
