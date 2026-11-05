import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { LibraryContext } from '../core/library-context';
import { FacetList, FieldInfo, LibraryInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { SearchBox } from '../shell/search-box';
import { searchTerms } from './facets';
import { SearchPage } from './search-page';

const FIELDS: FieldInfo[] = [{ id: 3, key: 'author', name: 'author', builtIn: true }, { id: 4, key: 'language', name: 'language', builtIn: true }];
const LIBRARY: LibraryInfo = { id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 };

@Component({ selector: 'lib-host', imports: [SearchBox], template: '<lib-search-box />' })
class Host {}

describe('Search', () => {
    async function open(url: string, api: ApiMock) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([LIBRARY]);
        api.fields.mockResolvedValue(FIELDS);
        api.books.mockResolvedValue({ items: [], next: null });
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        const router = TestBed.inject(Router);
        router.resetConfig([{ path: 'search', component: SearchPage }, { path: 'host', component: Host }]);
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

    it('lists the books matching the search in the library being browsed', async () => {
        const api = mockApi();
        const { element } = await open('/search?q=author%3A%22Terry%20Pratchett%22%20guards', api);

        expect(api.books).toHaveBeenCalledWith(expect.objectContaining({ library: 5, q: 'author:"Terry Pratchett" guards', after: null }));
        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Search');
        expect(element.querySelector('.browse-count')?.textContent).toBe('“author:"Terry Pratchett" guards” in Discworld');
        expect(element.querySelector('.empty')?.textContent).toContain('No books match this search.');
        expect(element.querySelector<HTMLDetailsElement>('.search-help')?.open).toBe(false);
    });

    it('shows how to search, each example opening its results, when nothing is asked', async () => {
        const api = mockApi();
        const { element } = await open('/search', api);

        expect(api.books).not.toHaveBeenCalled();
        expect(element.querySelector<HTMLDetailsElement>('.search-help')?.open).toBe(true);
        const field = [...element.querySelectorAll('.search-help a')].find(a => a.textContent === 'author:"Stephen King"');
        expect(field?.getAttribute('href')).toBe('/search?q=author:%22Stephen%20King%22');
    });

    describe('refine', () => {
        const FACETS: FacetList = {
            counted: 2000, capped: true, fields: [
                { field: 'author', values: [{ id: 9, name: 'Terry Pratchett', term: 'author:"Terry Pratchett"', count: 1200 }] },
                { field: 'language', values: [{ id: 11, name: 'en', term: 'language:"en"', count: 1900 }] },
                { field: 'format', values: [{ id: null, name: 'epub', term: 'format:epub', count: 800 }] },
            ],
        };

        it('splits a search into terms on the spaces outside quotes', () => {
            expect(searchTerms(' guards  author:"Terry Pratchett" -format:pdf ')).toEqual(['guards', 'author:"Terry Pratchett"', '-format:pdf']);
            expect(searchTerms('')).toEqual([]);
        });

        it('counts the matches, capped, and offers each value to narrow or leave out', async () => {
            const api = mockApi();
            api.facets.mockResolvedValue(FACETS);
            const { element } = await open('/search?q=guards', api);

            expect(api.facets).toHaveBeenCalledWith(5, 'guards');
            expect(element.querySelector('.search-count')?.textContent).toBe(' · 2,000+ books');
            const sheet = element.querySelector('lib-facets .sheet')!;
            expect(sheet.querySelector('.sheet-subtitle')?.textContent).toBe('The most frequent values among the first 2,000 books found');
            expect([...sheet.querySelectorAll('.facet-title')].map(t => t.textContent)).toEqual(['Authors', 'Languages', 'Formats']);
            const names = [...sheet.querySelectorAll('.facet-name')].map(n => n.textContent);
            expect(names).toEqual(['Terry Pratchett', 'English', 'EPUB']);
            const author = sheet.querySelector('.facet-value')!;
            expect(author.getAttribute('href')).toBe('/search?q=guards%20author:%22Terry%20Pratchett%22');
            expect(author.querySelector('.facet-count')?.textContent).toBe('1,200');
            expect(sheet.querySelector('.facet-exclude')?.getAttribute('href')).toBe('/search?q=guards%20-author:%22Terry%20Pratchett%22');
            expect(sheet.querySelector('.facet-exclude')?.getAttribute('aria-label')).toBe('Leave out Terry Pratchett');
        });

        it('marks a value the search has and takes it out', async () => {
            const api = mockApi();
            api.facets.mockResolvedValue(FACETS);
            const { element } = await open('/search?q=guards%20format%3Aepub', api);

            const epub = [...element.querySelectorAll('.facet-value')].find(a => a.textContent?.includes('EPUB'))!;
            expect(epub.getAttribute('aria-current')).toBe('true');
            expect(epub.getAttribute('href')).toBe('/search?q=guards');
            expect(element.querySelector('.facets-button')?.hasAttribute('disabled')).toBe(false);
        });

        it('stays disabled without values', async () => {
            const api = mockApi();
            api.facets.mockResolvedValue({ counted: 0, capped: false, fields: [] });
            const { element } = await open('/search?q=nothing', api);

            expect(element.querySelector('.facets-button')?.hasAttribute('disabled')).toBe(true);
            expect(element.querySelector('.search-count')?.textContent).toBe(' · 0 books');
        });
    });

    describe('the box', () => {
        async function type(element: HTMLElement, stable: () => Promise<void>, text: string) {
            const box = element.querySelector<HTMLInputElement>('lib-search-box input')!;
            box.focus();
            box.value = text;
            box.dispatchEvent(new Event('input'));
            await settle(200);
            await stable();
            return box;
        }

        function press(box: HTMLInputElement, key: string) {
            box.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
        }

        it('suggests for the last term and puts the one chosen in its place', async () => {
            const api = mockApi();
            api.suggestions.mockResolvedValue({
                from: 7, items: [{ kind: 'value', insert: 'author:"Terry Pratchett"', name: 'Terry Pratchett', field: 'author', bookCount: 41 }],
            });
            const { element, stable } = await open('/host', api);
            await TestBed.inject(LibraryContext).load();
            const box = await type(element, stable, 'guards pratch');

            expect(api.suggestions).toHaveBeenLastCalledWith('guards pratch', 5);
            const option = element.querySelector('[role="option"]')!;
            expect(option.querySelector('.suggestion-name')?.textContent).toBe('Terry Pratchett');
            expect(option.querySelector('.suggestion-kind')?.textContent).toBe('Author');
            expect(option.querySelector('.suggestion-count')?.textContent).toBe('41');
            expect(box.getAttribute('aria-expanded')).toBe('true');

            press(box, 'ArrowDown');
            await stable();
            expect(box.getAttribute('aria-activedescendant')).toBe('search-option-0');
            press(box, 'Enter');
            await stable();
            expect(box.value).toBe('guards author:"Terry Pratchett" ');
            expect(element.querySelector('[role="listbox"]')).toBeNull();
        });

        it('asks for a field\'s values once the field is chosen', async () => {
            const api = mockApi();
            api.suggestions.mockResolvedValue({ from: 0, items: [{ kind: 'field', insert: 'author:', name: 'author', field: null, bookCount: null }] });
            const { element, stable } = await open('/host', api);
            await TestBed.inject(LibraryContext).load();
            await type(element, stable, 'aut');

            expect(element.querySelector('[role="option"] .suggestion-name')?.textContent).toBe('Author');
            element.querySelector<HTMLElement>('[role="option"]')!.click();
            await settle(200);
            await stable();
            expect(element.querySelector<HTMLInputElement>('lib-search-box input')!.value).toBe('author:');
            expect(api.suggestions).toHaveBeenLastCalledWith('author:', 5);
        });

        it('opens the results on Enter, and Escape closes the suggestions', async () => {
            const api = mockApi();
            api.suggestions.mockResolvedValue({ from: 0, items: [{ kind: 'value', insert: 'tag:"Fav"', name: 'Fav', field: null, bookCount: 2 }] });
            const { element, router, stable } = await open('/host', api);
            await TestBed.inject(LibraryContext).load();
            const box = await type(element, stable, 'fav');

            press(box, 'Escape');
            await stable();
            expect(element.querySelector('[role="listbox"]')).toBeNull();
            box.form!.dispatchEvent(new Event('submit'));
            await stable();
            expect(router.url).toBe('/search?q=fav');
        });
    });
});
