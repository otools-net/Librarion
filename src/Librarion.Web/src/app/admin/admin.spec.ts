import { Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, groupInfo, mockApi, sessionInfo, settingCatalog, settingState, settle, testProviders, userInfo } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { Media } from '../core/media';
import { UnsavedChanges } from '../core/unsaved-changes';
import { RightInfo, ScheduleInfo, TaskRunInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { AdminPage } from './admin-page';
import { adminMatcher } from './admin-route';
import { GroupsSection } from './groups-section';
import { LibrariesSection } from './libraries-section';
import { LibraryPanel, LibraryTab } from './library-panel';
import { SchedulesTab } from './schedules-tab';
import { SecuritySection } from './security-section';
import { TenantsSection } from './tenants-section';
import { UsersSection } from './users-section';

async function render<T>(component: Type<T>, api: ApiMock, rights: string[] = ['tenant.users']) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(rights));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(component);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { fixture, element, navigate, stable: async () => { await settle(); await fixture.whenStable(); } };
}

function type(element: HTMLElement, selector: string, value: string): void {
    const input = element.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
}

function click(element: HTMLElement, text: string): void {
    [...element.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === text)!.click();
}

const right = (id: string, area: string, kind: 'read' | 'write' = 'read', scope: RightInfo['scope'] = 'tenant'): RightInfo =>
    ({ id, area, kind, scope, defaultGroups: [], disabledByServer: false });

describe('AdminPage', () => {
    /** The page at `url` through the real route matcher. */
    async function open(url: string, rights: string[], api = mockApi(), phone = false) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api, { provide: Media, useValue: { phone: signal(phone) } }) });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const router = TestBed.inject(Router);
        router.resetConfig([{ matcher: adminMatcher, component: AdminPage }, { path: '**', children: [] }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl(url);
        await settle();
        harness.detectChanges();
        await harness.fixture.whenStable();
        return { harness, router, element: harness.routeNativeElement as HTMLElement };
    }

    const navLinks = (element: HTMLElement) => [...element.querySelectorAll('.admin-nav a')].map(a => a.textContent?.trim());

    it('lists only the sections the session has a right for, by group, with the overview\'s cards', async () => {
        const { element } = await open('/admin', ['account.preferences', 'tenant.users', 'tenant.securityLog']);

        expect([...element.querySelectorAll('.admin-nav .group-label')].map(label => label.textContent?.trim()))
            .toEqual(['Account', 'People and access', 'Tenant and server']);
        expect(navLinks(element)).toEqual(['My account', 'Accounts', 'Groups', 'Security log']);
        expect(element.querySelector('h1')?.textContent).toBe('Administration');
        expect([...element.querySelectorAll('.admin-card .card-title')].map(a => a.textContent?.trim()))
            .toEqual(['My account', 'Accounts', 'Groups', 'Security log']);
    });

    it('opens a section with its sub-pages under it, breadcrumbs and the sub-page\'s heading', async () => {
        const { element } = await open('/admin/settings/server', ['tenant.settings', 'server.settings']);

        expect(navLinks(element)).toEqual(['Settings', 'This tenant', 'Whole server', 'System']);
        expect(element.querySelector('.admin-subnav a[aria-current="page"]')?.textContent?.trim()).toBe('Whole server');
        expect([...element.querySelectorAll('.admin-crumbs li')].map(li => li.textContent?.trim()))
            .toEqual(['Administration', 'Tenant and server', 'Settings', 'Whole server']);
        expect(element.querySelector('.admin-head h1')?.textContent).toBe('Whole server');
        expect(element.querySelector('.admin-head p')?.textContent).toContain('defaults of every tenant');
        expect(element.querySelector('lib-settings-section')).not.toBeNull();
    });

    it('keeps the addresses of the earlier pages (admin/users) and opens the first sub-page', async () => {
        const { element } = await open('/admin/users', ['tenant.users']);

        expect(element.querySelector('.admin-head h1')?.textContent).toBe('Accounts');
        expect(element.querySelector('.admin-nav a[aria-current="page"]')?.textContent?.trim()).toBe('Accounts');
        expect(element.querySelector('lib-users-section')).not.toBeNull();
    });

    it('sends a section the session can\'t see to the overview, and one section straight to it', async () => {
        const hidden = await open('/admin/tenants', ['account.preferences', 'tenant.users']);
        expect(hidden.router.url).toBe('/admin');

        const single = await open('/admin', ['account.preferences', 'account.password']);
        expect(single.router.url).toBe('/admin/account');
        expect(single.element.querySelector('.admin-head h1')?.textContent).toBe('Preferences');
    });

    it('on a phone, lists the sections with their descriptions instead of the cards', async () => {
        const { element } = await open('/admin', ['account.preferences', 'tenant.users'], mockApi(), true);

        const links = [...element.querySelectorAll<HTMLAnchorElement>('.admin-nav a')];
        expect(links.map(a => a.querySelector('.admin-nav-title')?.textContent)).toEqual(['My account', 'Accounts', 'Groups']);
        expect(links[1].querySelector('.admin-nav-description')?.textContent).toContain('Who can sign in');
        expect(links[1].querySelector('.admin-nav-chevron')).not.toBeNull();
        expect(element.querySelector('.admin-card')).toBeNull();
        expect(element.querySelector('h1')?.textContent).toBe('Administration');
    });

    it('on a phone, opens a section alone with its sub-pages as tabs that arrows move between', async () => {
        const { element, router, harness } = await open('/admin/settings', ['tenant.settings', 'server.settings'], mockApi(), true);

        expect(element.querySelector('.admin-nav')).toBeNull();
        const tabs = [...element.querySelectorAll<HTMLButtonElement>('.admin-chips [role="tab"]')];
        expect(tabs.map(tab => [tab.textContent?.trim(), tab.getAttribute('aria-selected'), tab.tabIndex])).toEqual([['This tenant', 'true', 0], ['Whole server', 'false', -1]]);
        expect(element.querySelector('#admin-panel')?.getAttribute('aria-labelledby')).toBe('admin-chip-tenant');

        tabs[1].click();
        await settle();
        harness.detectChanges();
        expect(router.url).toBe('/admin/settings/server');
        expect(element.querySelector('.admin-head h1')?.textContent).toBe('Whole server');

        element.querySelector('.admin-chips')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
        await settle();
        expect(router.url).toBe('/admin/settings/tenant');
    });

    it('opens a library\'s own pages: breadcrumbs, its name, the tabs its rights allow', async () => {
        const api = mockApi();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Books', created: '' }]);
        api.libraryRoots.mockResolvedValue([]);
        api.libraryTasks.mockResolvedValue([]);
        api.schedules.mockResolvedValue([]);
        api.missingBooks.mockResolvedValue({ items: [], next: null, total: 0, ready: 0, keepDays: 30 });
        const { element, harness } = await open('/admin/libraries/5/missing', ['tenant.libraries', 'library.scan'], api);
        await settle();
        harness.detectChanges();
        await harness.fixture.whenStable();

        expect([...element.querySelectorAll('.admin-crumbs li')].map(li => li.textContent?.trim())).toEqual(['Administration', 'Libraries', 'Libraries', 'Books']);
        expect(element.querySelector('.admin-head h1')?.textContent).toBe('Books');
        expect([...element.querySelectorAll('.library-tabs a')].map(a => a.textContent?.trim())).toEqual(['Folders', 'Runs', 'Missing books', 'Schedules']);
        expect(element.querySelector('.library-tabs a[aria-current="page"]')?.textContent?.trim()).toBe('Missing books');
        expect(element.querySelector('.empty')?.textContent).toContain('No missing books');
        expect(element.querySelector('.admin-nav a[aria-current="page"]')?.textContent?.trim()).toBe('Libraries');
    });

    it('opens the first allowed tab for an unknown one, and says when the library isn\'t there', async () => {
        const api = mockApi();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Books', created: '' }]);
        api.libraryRoots.mockResolvedValue([]);
        const { router, harness, element } = await open('/admin/libraries/5/import', ['tenant.libraries'], api);
        await settle();
        harness.detectChanges();
        expect(router.url).toBe('/admin/libraries/5/folders');
        expect([...element.querySelectorAll('.library-tabs a')].map(a => a.textContent?.trim())).toEqual(['Folders']);

        const gone = await open('/admin/libraries/9', ['tenant.libraries'], api);
        await settle();
        gone.harness.detectChanges();
        expect(gone.element.querySelector('.empty')?.textContent).toContain('doesn\'t exist');
    });

    it('marks the open section and sub-page while its form has unsaved changes', async () => {
        const { element, harness } = await open('/admin/settings/server', ['tenant.settings', 'server.settings']);
        expect(element.querySelector('.admin-mark')).toBeNull();

        TestBed.inject(UnsavedChanges).set({}, true, () => { });
        harness.detectChanges();
        await harness.fixture.whenStable();
        expect(element.querySelectorAll('.admin-nav .admin-mark')).toHaveLength(2);
        expect(element.querySelector('.admin-subnav a[aria-current="page"] .admin-mark')).not.toBeNull();
    });

    /** Types `query` in the column's search box. */
    async function find(element: HTMLElement, harness: RouterTestingHarness, query: string) {
        const box = element.querySelector<HTMLInputElement>('.admin-search input')!;
        box.dispatchEvent(new Event('focus'));
        box.value = query;
        box.dispatchEvent(new Event('input'));
        await settle();
        harness.detectChanges();
        await harness.fixture.whenStable();
        return box;
    }

    it('finds sections, sub-pages and settings from the column, the words marked, instead of the groups', async () => {
        const api = mockApi();
        api.settingCatalog.mockResolvedValue(settingCatalog());
        const { element, harness } = await open('/admin', ['account.preferences', 'tenant.users', 'tenant.settings'], api);

        const box = await find(element, harness, 'côlors');
        expect(api.settingCatalog).toHaveBeenCalledTimes(1);
        expect(element.querySelector('.admin-nav .group-label')).toBeNull();
        expect(element.querySelector('.admin-search-count')?.textContent).toBe('2 found');
        const results = [...element.querySelectorAll('.admin-search-results a')];
        expect(results.map(a => a.querySelector('.admin-search-where')?.textContent)).toEqual(['My account › Interface', 'Settings › Interface']);
        expect(results[0].querySelector('mark')?.textContent).toBe('Colors');

        await find(element, harness, 'nosuchthing');
        expect(element.querySelector('.admin-search-count')?.textContent).toBe('Nothing found');

        box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        harness.detectChanges();
        await harness.fixture.whenStable();
        expect(box.value).toBe('');
        expect(element.querySelector('.admin-search-results')).toBeNull();
        expect(element.querySelectorAll('.admin-nav .group-label').length).toBeGreaterThan(0);
        expect(api.settingCatalog).toHaveBeenCalledTimes(1);
    });

    it('opens a setting found on its page, scrolled to, focused and flashed', async () => {
        const api = mockApi();
        api.settingCatalog.mockResolvedValue(settingCatalog());
        api.settings.mockResolvedValue([settingState(), settingState({ key: 'ui.density', choices: ['compact', 'comfortable'], value: 'compact' })]);
        const { element, harness, router } = await open('/admin/settings/tenant', ['tenant.settings', 'server.settings'], api);

        await find(element, harness, 'comfortable');
        const result = [...element.querySelectorAll<HTMLAnchorElement>('.admin-search-results a')]
            .find(a => a.querySelector('.admin-search-where')?.textContent === 'Settings › Whole server › Interface')!;
        result.click();
        await settle();
        harness.detectChanges();
        await harness.fixture.whenStable();
        await settle();

        expect(router.url).toBe('/admin/settings/server');
        expect(api.settings).toHaveBeenLastCalledWith('server', undefined);
        expect(element.querySelector('#setting-row-ui\\.density')?.classList).toContain('setting-found');
        expect(document.activeElement?.id).toBe('setting-ui.density');

        // Enter opens the first one found.
        await find(element, harness, 'this tenant');
        element.querySelector('.admin-search input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
        await settle();
        expect(router.url).toBe('/admin/settings');
    });

    it('says when there is nothing to administer', async () => {
        const { element } = await open('/admin', []);

        expect(element.querySelector('.empty')?.textContent).toContain('Nothing to administer');
        expect(element.querySelector('.admin-nav')).toBeNull();
    });
});

describe('UsersSection', () => {
    it('lists the accounts with their groups, and creates one in the chosen groups', async () => {
        const api = mockApi();
        api.users.mockResolvedValue([userInfo({ groups: [{ groupId: 10, group: 'Users' }] }), userInfo({ id: 3, userName: 'cid', lockedOut: true })]);
        api.groups.mockResolvedValue([groupInfo(), groupInfo({ id: 11, name: 'Administrators', builtIn: 'administrators' })]);
        api.createUser.mockResolvedValue(userInfo({ id: 4, userName: 'dan' }));
        const { element, stable } = await render(UsersSection, api);

        const rows = [...element.querySelectorAll('.row')];
        expect(rows[0].textContent).toContain('bea · Users');
        expect(rows[1].textContent).toContain('Locked');

        click(element, 'New account');
        await stable();
        type(element, '#user-name', 'dan');
        type(element, '#user-password', 'long password');
        const users = element.querySelector<HTMLInputElement>('fieldset input[type="checkbox"]')!;
        users.checked = true;
        users.dispatchEvent(new Event('change'));
        element.querySelector('form.editor')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.createUser).toHaveBeenCalledWith(expect.objectContaining({ userName: 'dan', password: 'long password', groupIds: [10], isServerAdmin: false }));
        expect(element.querySelector('form.editor')).toBeNull();
        expect(api.users).toHaveBeenCalledTimes(2);
    });

    it('keeps the password when left empty, unlocks, and shows the server\'s refusals', async () => {
        const api = mockApi();
        api.users.mockResolvedValue([userInfo({ lockedOut: true })]);
        api.groups.mockResolvedValue([]);
        api.updateUser.mockRejectedValue(new ApiError(409, 'x', null, 'tenant.lastAdministrator'));
        const { element, stable } = await render(UsersSection, api);

        element.querySelector<HTMLButtonElement>('.row-main')!.click();
        await stable();
        click(element, 'Unlock');
        await stable();

        expect(api.updateUser).toHaveBeenCalledWith(2, expect.objectContaining({ userName: 'bea', password: null, unlock: true }));
        expect(element.querySelector('form.editor [role="alert"]')?.textContent).toContain('no administrator left');
    });

    it('lists the server administrators with server.users', async () => {
        const api = mockApi();
        api.users.mockResolvedValue([]);
        api.groups.mockResolvedValue([]);
        const { element, stable } = await render(UsersSection, api, ['tenant.users', 'server.users']);

        const admins = element.querySelector<HTMLInputElement>('.toolbar input[type="checkbox"]')!;
        admins.checked = true;
        admins.dispatchEvent(new Event('change'));
        await stable();

        expect(api.users).toHaveBeenLastCalledWith({ admins: true, search: '', after: undefined });
    });
});

describe('GroupsSection', () => {
    it('shows the grantable rights by area, marks those that change things, and saves a group', async () => {
        const api = mockApi();
        api.groups.mockResolvedValue([groupInfo({ rights: ['library.browse'], memberIds: [2] })]);
        api.rights.mockResolvedValue([right('library.browse', 'library', 'read', 'library'), right('tenant.users', 'tenant', 'write'), right('server.tenants', 'server', 'write', 'server')]);
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Comics', created: '' }]);
        api.users.mockResolvedValue([userInfo()]);
        api.updateGroup.mockResolvedValue(groupInfo());
        const { element, stable } = await render(GroupsSection, api);

        expect(element.querySelector('.row')?.textContent).toContain('1 rights · 1 members');
        element.querySelector<HTMLButtonElement>('.row-main')!.click();
        await stable();
        const legends = [...element.querySelectorAll('legend')].map(legend => legend.textContent?.trim());
        expect(legends).toEqual(['Libraries', 'Tenant', 'Where its library rights apply', 'Members']);
        expect(element.querySelector<HTMLInputElement>('#group-name')!.disabled).toBe(true);
        expect(element.textContent).toContain('Changes things');

        const manage = [...element.querySelectorAll<HTMLLabelElement>('label.check')].find(label => label.textContent?.includes('Manage accounts'))!.querySelector('input')!;
        manage.checked = true;
        manage.dispatchEvent(new Event('change'));
        element.querySelector('form.editor')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.updateGroup).toHaveBeenCalledWith(10, { name: 'Users', allLibraries: true, rights: ['library.browse', 'tenant.users'], libraryIds: [], memberIds: [2] });
    });
});

describe('LibrariesSection', () => {
    it('adds, renames and deletes libraries', async () => {
        const api = mockApi();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Books', created: '' }]);
        api.createLibrary.mockResolvedValue({ id: 6, tenantId: 1, name: 'Comics', created: '' });
        api.renameLibrary.mockResolvedValue({ id: 5, tenantId: 1, name: 'Novels', created: '' });
        api.deleteLibrary.mockResolvedValue(undefined);
        vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const { element, stable } = await render(LibrariesSection, api, ['tenant.libraries']);

        type(element, '.toolbar input', 'Comics');
        element.querySelector('form.toolbar')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(api.createLibrary).toHaveBeenCalledWith('Comics');

        click(element, 'Rename');
        await stable();
        type(element, '.row-form input', 'Novels');
        element.querySelector('.row-form')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(api.renameLibrary).toHaveBeenCalledWith(5, 'Novels');

        click(element, 'Delete');
        await stable();
        expect(api.deleteLibrary).toHaveBeenCalledWith(5);
        expect(TestBed.inject(ToastService).toasts().map(toast => toast.message)).toEqual(['Library Books deleted.']);
        vi.restoreAllMocks();
    });
});

describe('LibraryPanel', () => {
    const library = { id: 5, tenantId: 1, name: 'Books', created: '', bookCount: 0 };
    const run = (overrides: Partial<TaskRunInfo>): TaskRunInfo => ({
        id: 7, libraryId: 5, task: 'libraryScan', trigger: 'manual', started: '2026-11-01T10:00:00Z', finished: null, outcome: 'running',
        summary: null, counts: null, progress: null, waiting: false, ...overrides,
    });

    async function panel(api: ReturnType<typeof mockApi>, rights: string[], tab: LibraryTab = 'folders') {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const fixture = TestBed.createComponent(LibraryPanel);
        fixture.componentRef.setInput('library', library);
        fixture.componentRef.setInput('tab', tab);
        fixture.detectChanges();
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        const stable = async () => { await settle(); await fixture.whenStable(); };
        /** Another tab of the same panel: nothing is loaded again. */
        const show = async (other: LibraryTab) => { fixture.componentRef.setInput('tab', other); await stable(); };
        return { element, stable, show };
    }

    it('adds a folder, scans, follows the run and lists what it did', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([{ id: 1, libraryId: 5, path: '/srv/books', created: '', exists: false }]);
        api.schedules.mockResolvedValue([]);
        api.libraryTasks.mockResolvedValueOnce([run({ outcome: 'succeeded', finished: '2026-11-01T10:01:00Z',
            counts: { added: 3, changed: 0, removed: 0, unchanged: 9, archivesRead: 1, archivesSkipped: 0, unreadable: 0, missingRoots: [], problems: ['a.zip: broken'], missing: 1, returned: 2, skipped: 0 } })]);
        api.addLibraryRoot.mockResolvedValue({ id: 2, libraryId: 5, path: '/srv/more', created: '', exists: true });
        api.scanLibrary.mockResolvedValue({ runId: 8, started: true });
        const { element, stable, show } = await panel(api, ['tenant.libraries', 'library.scan']);

        expect(element.textContent).toContain('/srv/books');
        expect(element.querySelector('.badge.danger')?.textContent).toContain('missing');
        type(element, 'form.toolbar input', '/srv/more');
        element.querySelector('form.toolbar')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(api.addLibraryRoot).toHaveBeenCalledWith(5, '/srv/more');

        await show('runs');
        expect(element.textContent).toContain('3 added · 0 changed · 1 missing · 2 back · 0 removed · 9 unchanged');
        expect(element.querySelector('details')?.textContent).toContain('a.zip: broken');
        expect(element.textContent).not.toContain('/srv/books');
        expect(api.libraryRoots).toHaveBeenCalledTimes(2);

        api.libraryTasks.mockResolvedValue([run({ id: 8, progress: { filesSeen: 120, archivesDone: 1, added: 40, changed: 0, removed: 0, current: 'x.fb2', missing: 0, returned: 0, skipped: 0 } })]);
        click(element, 'Scan now');
        await stable();
        expect(api.scanLibrary).toHaveBeenCalledWith(5);
        // The progress is on the panel under the top bar (TaskPanel); here, a word that it runs.
        expect(api.runningTasks).toHaveBeenCalled();
        const scanning = [...element.querySelectorAll<HTMLButtonElement>('.scan-bar button')].find(b => b.textContent?.trim() === 'Scanning')!;
        expect(scanning.disabled).toBe(true);
        expect(element.querySelector('.scan-bar')?.textContent).toContain('its progress shows at the top of the page');
    });

    it('lists missing books and removes them only when the user accepts', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([]);
        api.schedules.mockResolvedValue([]);
        api.libraryTasks.mockResolvedValue([]);
        api.missingBooks.mockResolvedValue({
            items: [
                { id: 11, name: 'Old book', path: 'lib/a.zip/1.fb2', missingSince: '2026-09-01T10:00:00Z', ready: true },
                { id: 12, name: 'Recent book', path: 'b.fb2', missingSince: '2026-10-30T10:00:00Z', ready: false },
            ],
            next: null, total: 2, ready: 1, keepDays: 30,
        });
        api.removeMissingBooks.mockResolvedValue({ runId: 9 });
        const { element, stable } = await panel(api, ['tenant.libraries', 'library.scan'], 'missing');
        await stable();

        const list = element.querySelector('[aria-label="Missing books"]')!;
        expect(list.textContent).toContain('Old book');
        expect(list.textContent).toContain('ready to remove');
        expect(element.textContent).toContain('2 books whose files are gone');
        expect(element.textContent).toContain('1 missing for more than 30 days');

        const confirm = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(false);
        list.querySelector<HTMLButtonElement>('button.danger')!.click();
        await stable();
        expect(api.removeMissingBooks).not.toHaveBeenCalled();

        confirm.mockResolvedValue(true);
        list.querySelector<HTMLButtonElement>('button.danger')!.click();
        await stable();
        expect(api.removeMissingBooks).toHaveBeenCalledWith(5, { bookIds: [11] });
        click(element, 'Remove the 1 missing over 30 days');
        await stable();
        expect(api.removeMissingBooks).toHaveBeenCalledWith(5, { ready: true });
        click(element, 'Remove all 2');
        await stable();
        expect(api.removeMissingBooks).toHaveBeenCalledWith(5, { all: true });
        vi.restoreAllMocks();
    });

    it('starts an import from another program with its kind, source and files folder', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([]);
        api.schedules.mockResolvedValue([]);
        api.libraryTasks.mockResolvedValue([]);
        api.missingBooks.mockResolvedValue({ items: [], next: null, total: 0, ready: 0, keepDays: 30 });
        api.importKinds.mockResolvedValue(['calibre', 'inpx']);
        api.importIntoLibrary.mockResolvedValue({ runId: 4 });
        const { element, stable } = await panel(api, ['tenant.libraries', 'library.scan', 'library.import'], 'import');
        await stable();

        const select = element.querySelector<HTMLSelectElement>('select[aria-label="Kind of source"]')!;
        expect([...select.options].map(option => option.textContent?.trim())).toEqual(['Calibre library', 'INPX catalog']);
        select.value = 'inpx';
        select.dispatchEvent(new Event('change'));
        await stable();
        const source = element.querySelector<HTMLInputElement>('input[aria-label="Path of the source on the server"]')!;
        expect(source.placeholder).toBe('Path of the .inpx file');
        source.value = 'D:/books/librusec.inpx';
        source.dispatchEvent(new Event('input'));
        const files = element.querySelector<HTMLInputElement>(`input[aria-label="Folder of the books' files (optional)"]`)!;
        files.value = 'D:/books/archives';
        files.dispatchEvent(new Event('input'));
        await stable();
        click(element, 'Import');
        await stable();
        expect(api.importIntoLibrary).toHaveBeenCalledWith(5, { kind: 'inpx', source: 'D:/books/librusec.inpx', files: 'D:/books/archives' });
        expect(api.libraryRoots).toHaveBeenCalledTimes(2);
    });

    it('shows an import run with its own counts', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([]);
        api.schedules.mockResolvedValue([]);
        api.missingBooks.mockResolvedValue({ items: [], next: null, total: 0, ready: 0, keepDays: 30 });
        api.libraryTasks.mockResolvedValue([run({ id: 3, task: 'import', outcome: 'succeeded', finished: '2026-11-01T10:05:00Z',
            counts: { added: 5, changed: 2, removed: 0, unchanged: 7, archivesRead: 0, archivesSkipped: 0, unreadable: 0, missingRoots: [], problems: [], missing: 0, returned: 0, skipped: 1 } })]);
        const { element, stable } = await panel(api, ['tenant.libraries', 'library.scan'], 'runs');
        await stable();

        expect(element.textContent).toContain('Import from another source');
        expect(element.textContent).toContain('5 added · 2 updated · 7 unchanged · 1 skipped');
        expect(element.textContent).not.toContain('Import from another program');
    });

    it('shows only the folders without the scan right', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([]);
        const { element } = await panel(api, ['tenant.libraries']);
        expect(element.textContent).not.toContain('Scan now');
        expect(api.libraryTasks).not.toHaveBeenCalled();
    });
});

describe('SchedulesTab', () => {
    const library = { id: 5, tenantId: 1, name: 'Books', created: '', bookCount: 0 };
    const schedule = (overrides: Partial<ScheduleInfo>): ScheduleInfo => ({
        id: 1, libraryId: 5, task: 'libraryScan', importKind: null, source: null, files: null, kind: 'interval', start: '2026-11-01T03:00:00Z',
        intervalMinutes: 24 * 60, days: 0, enabled: true, lastRun: null, nextRun: '2026-11-02T03:00:00Z', ...overrides,
    });

    async function tab(api: ReturnType<typeof mockApi>, importKinds: string[] = []) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        const fixture = TestBed.createComponent(SchedulesTab);
        fixture.componentRef.setInput('library', library);
        fixture.componentRef.setInput('importKinds', importKinds);
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        return { element, stable: async () => { await settle(); await fixture.whenStable(); } };
    }

    it('says what each schedule runs first, then when, and whether it is on', async () => {
        const api = mockApi();
        api.schedules.mockResolvedValue([
            schedule({}),
            schedule({ id: 2, task: 'import', importKind: 'calibre', source: '/srv/calibre/metadata.db', files: '/srv/calibre', enabled: false }),
        ]);
        const { element } = await tab(api);

        const rows = [...element.querySelectorAll('.schedule-row')];
        expect(rows.map(row => row.querySelector('.row-title')?.textContent)).toEqual(['Folder scan', 'Import from Calibre library']);
        expect(rows[0].textContent).toContain('Every 24 h');
        expect(rows[0].textContent).toContain('next');
        expect(rows[1].textContent).toContain('/srv/calibre/metadata.db');
        expect(rows[1].textContent).toContain('off');
        expect(rows[1].classList).toContain('off');
        expect(rows[1].querySelector<HTMLInputElement>('input[role="switch"]')!.checked).toBe(false);
    });

    it('adds an import schedule in steps: what it runs, its source, when', async () => {
        const api = mockApi();
        api.schedules.mockResolvedValue([]);
        api.addSchedule.mockResolvedValue(schedule({}));
        const { element, stable } = await tab(api, ['calibre', 'inpx']);

        click(element, 'New schedule');
        await stable();
        const dialog = element.querySelector('dialog')!;
        expect([...dialog.querySelectorAll('.choice-card strong')].map(s => s.textContent)).toEqual(['Folder scan', 'Import from Calibre library', 'Import from INPX catalog']);
        expect(dialog.querySelector('#schedule-source')).toBeNull();
        expect([...dialog.querySelectorAll('legend')].map(l => l.textContent?.trim())).toEqual(['1What it runs', '2When']);

        const inpx = dialog.querySelectorAll<HTMLInputElement>('.choice-card input')[2];
        inpx.dispatchEvent(new Event('change'));
        await stable();
        expect([...dialog.querySelectorAll('legend')].map(l => l.textContent?.trim())).toEqual(['1What it runs', '2Where the import comes from', '3When']);
        expect(dialog.querySelector('label[for="schedule-source"]')?.textContent).toBe('Path of the .inpx file');
        const save = [...dialog.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === 'Save')!;
        expect(save.disabled).toBe(true);
        type(dialog, '#schedule-source', '/srv/librusec/librusec.inpx');
        click(dialog, 'On days of the week');
        await stable();
        dialog.querySelectorAll<HTMLInputElement>('.day-checks input')[0].dispatchEvent(new Event('change'));
        await stable();
        expect(save.disabled).toBe(false);
        dialog.querySelector('form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.addSchedule).toHaveBeenCalledWith(5, expect.objectContaining({
            task: 'import', importKind: 'inpx', source: '/srv/librusec/librusec.inpx', files: null, kind: 'weekly', days: 0b111_1110, intervalMinutes: 0, enabled: true,
        }));
        expect(TestBed.inject(ToastService).toasts().map(t => t.message)).toEqual(['Schedule added.']);
        expect(element.querySelector('dialog')).toBeNull();
    });

    it('shows the server\'s refusal next to the source, the dialog staying open', async () => {
        const api = mockApi();
        api.schedules.mockResolvedValue([]);
        api.addSchedule.mockRejectedValue(new ApiError(400, 'x', { source: ['source.missing'] }));
        const { element, stable } = await tab(api, ['calibre']);

        click(element, 'New schedule');
        await stable();
        element.querySelectorAll<HTMLInputElement>('.choice-card input')[1].dispatchEvent(new Event('change'));
        await stable();
        type(element, '#schedule-source', '/srv/none/metadata.db');
        await stable();
        element.querySelector('dialog form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(element.querySelector('#schedule-source')?.getAttribute('aria-invalid')).toBe('true');
        expect(element.querySelector('dialog .field-error')?.textContent).toContain('can\'t find this file');
    });

    it('turns a schedule off with its switch, keeping what it runs and when', async () => {
        const api = mockApi();
        const calibre = schedule({ id: 2, task: 'import', importKind: 'calibre', source: '/srv/c/metadata.db', files: '/srv/c' });
        api.schedules.mockResolvedValue([calibre]);
        api.updateSchedule.mockResolvedValue({ ...calibre, enabled: false, nextRun: null });
        const { element, stable } = await tab(api, ['calibre']);

        const toggle = element.querySelector<HTMLInputElement>('input[role="switch"]')!;
        expect(toggle.getAttribute('aria-label')).toBe('Import from Calibre library on');
        toggle.dispatchEvent(new Event('change'));
        await stable();

        expect(api.updateSchedule).toHaveBeenCalledWith(5, 2, expect.objectContaining({ task: 'import', importKind: 'calibre', source: '/srv/c/metadata.db', files: '/srv/c', enabled: false }));
        expect(element.querySelector('.schedule-row')?.classList).toContain('off');
    });
});

describe('TenantsSection', () => {
    it('switches the session to another tenant, and deletes one only when its name is typed', async () => {
        const api = mockApi();
        api.tenants.mockResolvedValue([
            { id: 1, slug: 'home', name: 'Home', disabled: false, created: '', disabledRights: [] },
            { id: 2, slug: 'club', name: 'Club', disabled: false, created: '', disabledRights: [] },
        ]);
        api.rights.mockResolvedValue([]);
        api.switchTenant.mockResolvedValue(sessionInfo([], { tenant: { id: 2, slug: 'club', name: 'Club' } }));
        api.deleteTenant.mockResolvedValue(undefined);
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(false);
        const { element, stable } = await render(TenantsSection, api, ['server.tenants']);

        expect(element.querySelectorAll('.row')[0].textContent).toContain('Working here');
        click(element, 'Work here');
        await stable();
        expect(api.switchTenant).toHaveBeenCalledWith(2);

        [...element.querySelectorAll<HTMLButtonElement>('button.danger')][0].click();
        await stable();
        expect(api.deleteTenant).not.toHaveBeenCalled();
        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ typeToConfirm: 'Home', danger: true }));
        ask.mockResolvedValue(true);
        [...element.querySelectorAll<HTMLButtonElement>('button.danger')][0].click();
        await stable();
        expect(api.deleteTenant).toHaveBeenCalledWith(1);
        vi.restoreAllMocks();
    });

    it('switches rights off for a tenant, those the server switches off shown and fixed', async () => {
        const api = mockApi();
        api.tenants.mockResolvedValue([{ id: 1, slug: 'home', name: 'Home', disabled: false, created: '', disabledRights: ['tenant.users'] }]);
        api.rights.mockResolvedValue([
            right('library.browse', 'library', 'read', 'library'), right('tenant.users', 'tenant', 'write'),
            { ...right('tenant.libraries', 'tenant', 'write'), disabledByServer: true }, right('server.tenants', 'server', 'write', 'server'),
        ]);
        api.updateTenant.mockResolvedValue({ id: 1, slug: 'home', name: 'Home', disabled: false, created: '', disabledRights: [] });
        const { element, stable } = await render(TenantsSection, api, ['server.tenants']);

        click(element, 'Edit');
        await stable();
        const box = (text: string) => [...element.querySelectorAll<HTMLLabelElement>('label.check')].find(label => label.textContent?.includes(text))!.querySelector('input')!;
        expect(box('Manage accounts').checked).toBe(true);
        expect(box('Manage libraries').disabled).toBe(true);
        expect(element.textContent).not.toContain('Manage tenants');

        box('Browse').click();
        box('Manage accounts').click();
        await stable();
        element.querySelector<HTMLFormElement>('form.editor')!.requestSubmit();
        await stable();
        expect(api.updateTenant).toHaveBeenCalledWith(1, { name: 'Home', disabled: false, disabledRights: ['library.browse'] });
        expect(element.querySelector('form.editor')).toBeNull();
    });
});

describe('SecuritySection', () => {
    it('lists the events newest first, filters by kind, and loads the next page from the last id', async () => {
        const api = mockApi();
        const page = Array.from({ length: 100 }, (_, i) => ({ id: 200 - i, time: '2026-10-31T10:00:00Z', kind: 'signedIn', tenantId: 1, userId: 1, userName: 'ana', address: '::1', detail: null }));
        api.securityEvents.mockResolvedValue(page);
        const { element, stable } = await render(SecuritySection, api, ['tenant.securityLog']);

        expect(element.querySelector('.row-title')?.textContent).toBe('Signed in');
        click(element, 'Show more');
        await stable();
        expect(api.securityEvents).toHaveBeenLastCalledWith({ before: 101, kind: '', user: '', all: false });

        const kind = element.querySelector<HTMLSelectElement>('select')!;
        kind.value = 'lockedOut';
        kind.dispatchEvent(new Event('change'));
        await stable();
        expect(api.securityEvents).toHaveBeenLastCalledWith({ before: undefined, kind: 'lockedOut', user: '', all: false });
        expect(element.querySelector('input[type="checkbox"]')).toBeNull();
    });
});
