import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { FolderDetail, FolderItem } from '../core/models';
import { LibraryContext } from '../core/library-context';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { FolderPage } from './folder-page';

const item = (id: number, name: string, overrides: Partial<FolderItem> = {}): FolderItem =>
    ({ id, name, zip: false, files: 0, totalFiles: 3, folders: 0, defaultTags: 0, ...overrides });
const detail = (folder: FolderItem, path: { id: number; name: string }[] = [], libraryId = 5): FolderDetail => ({ folder, libraryId, path });

describe('The folder view', () => {
    async function open(url: string, api: ApiMock, rights = ['library.browse', 'library.folders']) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([
            { id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 },
            { id: 6, tenantId: 1, name: 'Comics', created: '', bookCount: 10 },
        ]);
        api.books.mockResolvedValue({ items: [], next: null });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const router = TestBed.inject(Router);
        router.resetConfig([
            { path: 'folders', component: FolderPage },
            { path: 'folders/:id', component: FolderPage },
            { path: 'folders/:id/:name', component: FolderPage },
        ]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl(url);
        const stable = async (wait = 0) => {
            for (let i = 0; i < 4; i++) {
                await settle(i === 0 ? wait : 0);
                harness.detectChanges();
                await harness.fixture.whenStable();
            }
        };
        await stable();
        return { harness, router, element: harness.routeNativeElement as HTMLElement, stable };
    }

    it('opens a library with one root at that root: its folders with their counts, each a readable address', async () => {
        const api = mockApi();
        const root = item(1, 'books', { totalFiles: 1234, folders: 2 });
        api.libraryFolders.mockResolvedValue([root]);
        api.folder.mockResolvedValue(detail(root));
        api.folders.mockResolvedValue({ items: [item(2, 'Authors', { totalFiles: 1200, folders: 40 }), item(3, 'fb2-0001.zip', { zip: true, files: 34, totalFiles: 34 })], next: null });
        const { element } = await open('/folders', api);

        expect(api.folders).toHaveBeenCalledWith(1, null, 100);
        expect(element.querySelector('h1')?.textContent?.trim()).toBe('Folders');
        expect(element.querySelector('.browse-count')?.textContent).toBe('2 folders · 1,234 files');
        const rows = [...element.querySelectorAll('.folder-row a')];
        expect(rows.map(r => r.querySelector('.value-name')?.textContent)).toEqual(['Authors', 'fb2-0001.zip']);
        expect(rows[0].getAttribute('href')).toBe('/folders/2/Authors');
        expect(rows[0].querySelector('.value-count')?.getAttribute('aria-label')).toBe('1,200 files');
        expect(rows[1].querySelector('use')?.getAttribute('href')).toBe('#icon-zip');
        expect(element.querySelector('lib-book-list'), 'the root has no books of its own').toBeNull();
    });

    it('shows a folder with the folders above it, more folders by key, then its own books', async () => {
        const api = mockApi();
        const king = item(9, 'King', { files: 2, totalFiles: 5, folders: 1 });
        api.folder.mockResolvedValue(detail(king, [{ id: 2, name: 'Authors' }]));
        api.folders.mockResolvedValueOnce({ items: [item(10, 'Dark Tower')], next: 'k1' });
        api.folders.mockResolvedValueOnce({ items: [item(11, 'Early')], next: null });
        const { element, stable } = await open('/folders/9/King', api);

        const crumbs = [...element.querySelectorAll('.folder-crumbs a')];
        expect(crumbs.map(c => c.textContent)).toEqual(['Discworld', 'Authors']);
        expect(crumbs[1].getAttribute('href')).toBe('/folders/2/Authors');
        expect(element.querySelector('.browse-count')?.textContent).toBe('1 folder · 5 files · 2 here');
        (element.querySelector('.folders-more button') as HTMLButtonElement).click();
        await stable();
        expect(api.folders).toHaveBeenLastCalledWith(9, 'k1', 100);
        expect([...element.querySelectorAll('.folder-row .value-name')].map(n => n.textContent)).toEqual(['Dark Tower', 'Early']);
        expect(element.querySelector('.folders-more')).toBeNull();
        expect(api.books).toHaveBeenCalledWith(expect.objectContaining({ library: 5, folder: 9 }));
    });

    it('puts the current name in the address and opens another library\'s folder in that library', async () => {
        const api = mockApi();
        api.folder.mockResolvedValue(detail(item(9, 'King', { files: 1 })));
        api.folders.mockResolvedValue({ items: [], next: null });
        const { router } = await open('/folders/9/Old%20name', api);
        expect(router.url).toBe('/folders/9/King');

        const other = mockApi();
        other.folder.mockResolvedValue(detail(item(30, 'Comics'), [], 6));
        other.folders.mockResolvedValue({ items: [], next: null });
        await open('/folders/30', other);
        expect(TestBed.inject(LibraryContext).current()?.id).toBe(6);
    });

    it('says a folder isn\'t there, and lists several roots at the top', async () => {
        const api = mockApi();
        api.folder.mockRejectedValue(new ApiError(404, 'Not found'));
        api.folders.mockRejectedValue(new ApiError(404, 'Not found'));
        const { element } = await open('/folders/77', api);
        expect(element.querySelector('.empty')?.textContent).toContain('isn\'t there any more');

        const roots = mockApi();
        roots.libraryFolders.mockResolvedValue([item(1, 'disk-a'), item(2, 'disk-b')]);
        const top = await open('/folders', roots);
        expect([...top.element.querySelectorAll('.folder-row .value-name')].map(n => n.textContent)).toEqual(['disk-a', 'disk-b']);
        expect(roots.folder).not.toHaveBeenCalled();
    });

    it('shows the default tags of a folder, its own and those from above, and a badge on rows that have some', async () => {
        const api = mockApi();
        const king = item(9, 'King', { files: 2, totalFiles: 2 });
        api.folder.mockResolvedValue(detail(king, [{ id: 2, name: 'Authors' }]));
        api.folders.mockResolvedValue({ items: [item(10, 'Horror', { defaultTags: 2 }), item(11, 'Plain')], next: null });
        api.defaultTags.mockResolvedValue({
            own: [{ tagId: 1, name: 'Kingverse', color: '#c00', folderId: 9, from: null }],
            inherited: [{ tagId: 2, name: 'Fiction', color: null, folderId: null, from: null }],
        });
        const { element } = await open('/folders/9/King', api);

        expect(api.defaultTags).toHaveBeenCalledWith(5, 9);
        const chips = [...element.querySelectorAll('.folder-defaults .chip')];
        expect(chips.map(c => c.textContent?.trim())).toEqual(['Kingverse', 'Fiction']);
        expect(chips[1].getAttribute('title')).toBe('From the library');
        expect(element.querySelector('.folder-defaults button'), 'changing them needs the right').toBeNull();
        const badges = element.querySelectorAll('.default-badge');
        expect(badges.length).toBe(1);
        expect(badges[0].getAttribute('aria-label')).toBe('2 default tags');
    });

    it('adds a default tag for the books there too, and takes one off asking whether the books keep it', async () => {
        const api = mockApi();
        const king = item(9, 'King', { files: 2, totalFiles: 25000 });
        api.folder.mockResolvedValue(detail(king));
        api.folders.mockResolvedValue({ items: [], next: null });
        const list = { own: [{ tagId: 1, name: 'Kingverse', color: null, folderId: 9, from: null }], inherited: [] };
        api.defaultTags.mockResolvedValue(list);
        api.pickTags.mockResolvedValue({ items: [{ id: 7, name: 'Horror', color: null, field: null, shorthand: null, alias: null, parent: null, books: 3 }], exact: true });
        api.addDefaultTag.mockResolvedValue({ runId: 77, logId: null, links: 25000 });
        api.removeDefaultTag.mockResolvedValue({ runId: null, logId: null, links: 2 });
        const { element, stable } = await open('/folders/9/King', api, ['library.browse', 'library.folders', 'library.defaultTags']);
        const follow = vi.spyOn(TestBed.inject(TaskWatch), 'follow').mockImplementation(() => { });

        element.querySelector<HTMLButtonElement>('.folder-defaults button')!.click();
        await stable();
        const box = element.querySelector<HTMLInputElement>('#default-tag-add')!;
        box.focus();
        box.value = 'hor';
        box.dispatchEvent(new Event('input'));
        await stable(200);
        element.querySelector<HTMLElement>('#default-tag-add-list [role="option"]')!.click();
        await stable();
        const choices = [...element.querySelectorAll<HTMLInputElement>('.default-tags-when input')];
        expect(choices.map(c => c.parentElement?.textContent?.trim())).toEqual(['New books only', 'Also the 25,000 files already there']);
        choices[1].click();
        await stable();
        element.querySelector<HTMLButtonElement>('button[form="default-tags-form"]')!.click();
        await stable();
        expect(api.addDefaultTag).toHaveBeenCalledWith(5, { folder: 9, tag: { id: 7 }, existing: true });
        expect(follow).toHaveBeenCalledWith(77);

        element.querySelector<HTMLButtonElement>('.default-tag-row .icon-button')!.click();
        await stable();
        expect(element.querySelector('.default-tag-ask span')?.textContent).toBe('Do the books here keep Kingverse?');
        [...element.querySelectorAll<HTMLButtonElement>('.default-tag-ask button')].find(b => b.textContent?.trim() === 'Take it off them')!.click();
        await stable();
        expect(api.removeDefaultTag).toHaveBeenCalledWith(5, 1, 9, true);
    });

    it('changes the own default tags of the library at the top', async () => {
        const api = mockApi();
        const root = item(1, 'books', { folders: 1 });
        api.libraryFolders.mockResolvedValue([root]);
        api.folder.mockResolvedValue(detail(root));
        api.folders.mockResolvedValue({ items: [item(2, 'Authors')], next: null });
        api.defaultTags.mockResolvedValue({ own: [], inherited: [] });
        await open('/folders', api, ['library.browse', 'library.folders', 'library.defaultTags']);
        expect(api.defaultTags).toHaveBeenCalledWith(5, null);
    });
});
