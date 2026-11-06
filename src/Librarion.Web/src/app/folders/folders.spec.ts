import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { FolderDetail, FolderItem } from '../core/models';
import { LibraryContext } from '../core/library-context';
import { SessionService } from '../core/session.service';
import { FolderPage } from './folder-page';

const item = (id: number, name: string, overrides: Partial<FolderItem> = {}): FolderItem =>
    ({ id, name, zip: false, files: 0, totalFiles: 3, folders: 0, ...overrides });
const detail = (folder: FolderItem, path: { id: number; name: string }[] = [], libraryId = 5): FolderDetail => ({ folder, libraryId, path });

describe('The folder view', () => {
    async function open(url: string, api: ApiMock) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        localStorage.clear();
        api.libraries.mockResolvedValue([
            { id: 5, tenantId: 1, name: 'Discworld', created: '', bookCount: 120 },
            { id: 6, tenantId: 1, name: 'Comics', created: '', bookCount: 10 },
        ]);
        api.books.mockResolvedValue({ items: [], next: null });
        TestBed.inject(SessionService).set(sessionInfo(['library.browse', 'library.folders']));
        const router = TestBed.inject(Router);
        router.resetConfig([
            { path: 'folders', component: FolderPage },
            { path: 'folders/:id', component: FolderPage },
            { path: 'folders/:id/:name', component: FolderPage },
        ]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl(url);
        const stable = async () => {
            for (let i = 0; i < 4; i++) {
                await settle();
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
});
