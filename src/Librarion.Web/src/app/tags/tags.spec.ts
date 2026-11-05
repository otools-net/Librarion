import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { FieldInfo, FieldValue, TagDetail } from '../core/models';
import { LibraryContext } from '../core/library-context';
import { SessionService } from '../core/session.service';
import { FieldsPage } from './fields-page';
import { TagPage } from './tag-page';
import { ValuesPage } from './values-page';

const FIELDS: FieldInfo[] = [
    { id: 3, key: 'author', name: 'author', builtIn: true },
    { id: 7, key: 'series', name: 'series', builtIn: true },
    { id: 11, key: 'language', name: 'language', builtIn: true },
    { id: 40, key: 'mood', name: 'Mood', builtIn: false },
];
const value = (id: number, name: string, bookCount: number | null = 3): FieldValue => ({ id, name, sortName: name, bookCount });
const tag = (overrides: Partial<TagDetail> = {}): TagDetail => ({
    id: 21, name: 'Terry Pratchett', sortName: 'Pratchett, Terry', field: 'author', fieldName: 'author', isCategory: false, bookCount: 41,
    parents: [], children: [], moreChildren: false, ...overrides,
});

describe('Browsing by value', () => {
    async function open(url: string, api: ApiMock) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 }]);
        api.fields.mockResolvedValue(FIELDS);
        api.books.mockResolvedValue({ items: [], next: null });
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        const router = TestBed.inject(Router);
        router.resetConfig([
            { path: 'fields', component: FieldsPage },
            { path: 'fields/:key', component: ValuesPage },
            { path: 'tags', component: ValuesPage },
            { path: 'tags/:id', component: TagPage },
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
        return { harness, router, element: harness.routeNativeElement as HTMLElement, stable };
    }

    it('lists a field\'s values A–Z with their books in the library being browsed, each opening its page', async () => {
        const api = mockApi();
        api.tagValues.mockResolvedValue({ items: [value(21, 'Terry Pratchett', 41), value(22, 'Neil Gaiman', 1234)], next: null });
        const { element } = await open('/fields/author', api);

        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Authors');
        expect(element.querySelector('.browse-count')?.textContent).toBe('With books in Discworld');
        expect(api.tagValues).toHaveBeenLastCalledWith('author', { library: 5, from: '', after: null, limit: 100 });
        const rows = [...element.querySelectorAll('.value a')];
        expect(rows.map(r => r.querySelector('.value-name')?.textContent)).toEqual(['Terry Pratchett', 'Neil Gaiman']);
        expect(rows[1].querySelector('.value-count')?.textContent).toBe('1,234');
        expect(rows[0].getAttribute('href')).toBe('/tags/21');
    });

    it('goes on through short pages until the server says the list ends', async () => {
        const api = mockApi();
        api.tagValues.mockResolvedValueOnce({ items: [], next: 'k1' });
        api.tagValues.mockResolvedValueOnce({ items: [value(1, 'Zoe')], next: null });
        const { element } = await open('/tags', api);

        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Tags');
        expect(api.tagValues).toHaveBeenCalledTimes(2);
        expect(api.tagValues).toHaveBeenLastCalledWith(null, expect.objectContaining({ after: 'k1' }));
        expect(element.querySelectorAll('.value')).toHaveLength(1);
    });

    it('starts the list where "Go to" says, keeping it in the address', async () => {
        const api = mockApi();
        api.tagValues.mockResolvedValue({ items: [value(1, 'Pratchett')], next: null });
        const { element, router, stable } = await open('/fields/author', api);

        const box = element.querySelector<HTMLInputElement>('.values-jump input')!;
        box.value = 'pra';
        box.dispatchEvent(new Event('input'));
        box.form!.dispatchEvent(new Event('submit'));
        await stable();
        expect(router.url).toBe('/fields/author?from=pra');
        expect(api.tagValues).toHaveBeenLastCalledWith('author', expect.objectContaining({ from: 'pra', after: null }));
    });

    it('keeps what was typed when the list starts again before the jump (the libraries arriving meanwhile)', async () => {
        const api = mockApi();
        api.tagValues.mockResolvedValue({ items: [value(1, 'Pratchett')], next: null });
        const { element, router, stable } = await open('/fields/author', api);
        const libraries = TestBed.inject(LibraryContext);
        api.libraries.mockResolvedValue([
            { id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 }, { id: 6, tenantId: 1, name: 'Comics', created: '', bookCount: 3 },
        ]);
        await libraries.load();

        const box = element.querySelector<HTMLInputElement>('.values-jump input')!;
        box.value = 'pra';
        box.dispatchEvent(new Event('input'));
        libraries.choose(6);
        await stable();
        await settle(350);
        await stable();
        expect(router.url).toBe('/fields/author?from=pra');
    });

    it('names languages by their name and custom fields by theirs; an unknown field says so', async () => {
        const api = mockApi();
        api.tagValues.mockResolvedValue({ items: [value(1, 'ru')], next: null });
        const languages = await open('/fields/language', api);
        expect(languages.element.querySelector('.value-name')?.textContent).toBe('Russian');

        const custom = mockApi();
        custom.tagValues.mockResolvedValue({ items: [], next: null });
        const mood = await open('/fields/mood', custom);
        expect(mood.element.querySelector('h1')?.textContent?.trim()).toBe('Mood');
        expect(mood.element.querySelector('.empty')?.textContent).toContain('Nothing here has books in this library yet.');

        const missing = mockApi();
        missing.tagValues.mockRejectedValue(new ApiError(404, 'Not found'));
        const unknown = await open('/fields/nope', missing);
        expect(unknown.element.querySelector('.empty')?.textContent).toContain('There is no such field.');
    });

    it('shows a tag: its field, count, the tags around it, and its books', async () => {
        const api = mockApi();
        api.tag.mockResolvedValue(tag({
            parents: [{ id: 2, name: 'Fantasy', field: 'category' }], children: [{ id: 9, name: 'Discworld', field: null }], moreChildren: true,
        }));
        const { element } = await open('/tags/21', api);

        expect(api.tag).toHaveBeenLastCalledWith(21, 5);
        expect(element.querySelector('.tag-kind')?.textContent?.trim()).toBe('Author');
        expect(element.querySelector('.tag-kind')?.getAttribute('href')).toBe('/fields/author');
        expect(element.querySelector('h1')?.textContent).toBe('Terry Pratchett');
        expect(element.querySelector('.browse-count')?.textContent).toBe('41 books in Discworld');
        expect([...element.querySelectorAll('.tag-related .chip')].map(c => c.textContent)).toEqual(['Fantasy', 'Discworld']);
        expect(element.querySelector('.tag-related-more')).not.toBeNull();
        expect(api.books).toHaveBeenLastCalledWith(expect.objectContaining({ library: 5, tag: 21, sort: 'title' }));
        expect(element.querySelector('.empty')?.textContent).toContain('No books with it in this library.');
    });

    it('lists a series by its numbers first', async () => {
        const api = mockApi();
        api.tag.mockResolvedValue(tag({ id: 30, name: 'Rincewind', field: 'series', fieldName: 'series' }));
        const { element } = await open('/tags/30', api);

        expect(api.books).toHaveBeenLastCalledWith(expect.objectContaining({ tag: 30, sort: 'number' }));
        expect(element.querySelector<HTMLSelectElement>('.browse-sort select')?.value).toBe('number');
    });

    it('says when a tag is not there (or not shown)', async () => {
        const api = mockApi();
        api.tag.mockRejectedValue(new ApiError(404, 'Not found'));
        const { element } = await open('/tags/999', api);
        expect(element.querySelector('h1')?.textContent).toBe('Not found');
    });

    it('lists the fields, then the tags', async () => {
        const api = mockApi();
        const { element } = await open('/fields', api);
        const cards = [...element.querySelectorAll('.field-card')];
        expect(cards.map(c => c.querySelector('.field-card-name')?.textContent)).toEqual(['Authors', 'Series', 'Languages', 'Mood', 'Tags']);
        expect(cards[3].querySelector('.field-card-key')?.textContent).toBe('mood');
        expect(cards[4].getAttribute('href')).toBe('/tags');
    });
});
