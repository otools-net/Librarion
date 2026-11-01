import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ApiMock, groupInfo, mockApi, sessionInfo, settle, testProviders, userInfo } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { RightInfo, TaskRunInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { AdminPage } from './admin-page';
import { GroupsSection } from './groups-section';
import { LibrariesSection } from './libraries-section';
import { LibraryPanel } from './library-panel';
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
    it('shows only the sections the session has a right for', async () => {
        const { element, navigate } = await render(AdminPage, mockApi(), ['tenant.users', 'tenant.securityLog']);

        expect([...element.querySelectorAll('.tab')].map(tab => tab.textContent?.trim())).toEqual(['Accounts', 'Groups', 'Security log']);
        expect(navigate).toHaveBeenCalledWith(['users'], expect.objectContaining({ replaceUrl: true }));
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
        vi.spyOn(window, 'confirm').mockReturnValue(true);
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
        vi.restoreAllMocks();
    });
});

describe('LibraryPanel', () => {
    const library = { id: 5, tenantId: 1, name: 'Books', created: '', bookCount: 0 };
    const run = (overrides: Partial<TaskRunInfo>): TaskRunInfo => ({
        id: 7, libraryId: 5, task: 'libraryScan', trigger: 'manual', started: '2026-11-01T10:00:00Z', finished: null, outcome: 'running',
        summary: null, counts: null, progress: null, waiting: false, ...overrides,
    });

    async function panel(api: ReturnType<typeof mockApi>, rights: string[]) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const fixture = TestBed.createComponent(LibraryPanel);
        fixture.componentRef.setInput('library', library);
        fixture.detectChanges();
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        return { element, stable: async () => { await settle(); await fixture.whenStable(); } };
    }

    it('adds a folder, scans, follows the run and lists what it did', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([{ id: 1, libraryId: 5, path: '/srv/books', created: '', exists: false }]);
        api.schedules.mockResolvedValue([]);
        api.libraryTasks.mockResolvedValueOnce([run({ outcome: 'succeeded', finished: '2026-11-01T10:01:00Z',
            counts: { added: 3, changed: 0, removed: 0, unchanged: 9, archivesRead: 1, archivesSkipped: 0, unreadable: 0, missingRoots: [], problems: ['a.zip: broken'], missing: 1, returned: 2 } })]);
        api.addLibraryRoot.mockResolvedValue({ id: 2, libraryId: 5, path: '/srv/more', created: '', exists: true });
        api.scanLibrary.mockResolvedValue({ runId: 8, started: true });
        const { element, stable } = await panel(api, ['tenant.libraries', 'library.scan']);

        expect(element.textContent).toContain('/srv/books');
        expect(element.querySelector('.badge.danger')?.textContent).toContain('missing');
        expect(element.textContent).toContain('3 added · 0 changed · 1 missing · 2 back · 0 removed · 9 unchanged');
        expect(element.querySelector('details')?.textContent).toContain('a.zip: broken');

        type(element, 'form.toolbar input', '/srv/more');
        element.querySelector('form.toolbar')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(api.addLibraryRoot).toHaveBeenCalledWith(5, '/srv/more');

        api.libraryTasks.mockResolvedValue([run({ id: 8, progress: { filesSeen: 120, archivesDone: 1, added: 40, changed: 0, removed: 0, current: 'x.fb2', missing: 0, returned: 0 } })]);
        click(element, 'Scan now');
        await stable();
        expect(api.scanLibrary).toHaveBeenCalledWith(5);
        expect(element.querySelector('[role="status"]')?.textContent).toContain('120 files seen · 40 added');
        expect(element.textContent).toContain('Cancel');
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
        const { element, stable } = await panel(api, ['tenant.libraries', 'library.scan']);
        await stable();

        const list = element.querySelector('[aria-label="Missing books"]')!;
        expect(list.textContent).toContain('Old book');
        expect(list.textContent).toContain('ready to remove');
        expect(element.textContent).toContain('2 books whose files are gone');
        expect(element.textContent).toContain('1 missing for more than 30 days');

        const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
        list.querySelector<HTMLButtonElement>('button.danger')!.click();
        await stable();
        expect(api.removeMissingBooks).not.toHaveBeenCalled();

        confirm.mockReturnValue(true);
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

    it('shows only the folders without the scan right, and adds a weekly schedule', async () => {
        const api = mockApi();
        api.libraryRoots.mockResolvedValue([]);
        const { element } = await panel(api, ['tenant.libraries']);
        expect(element.textContent).not.toContain('Scan now');
        expect(api.libraryTasks).not.toHaveBeenCalled();

        const scheduling = mockApi();
        scheduling.libraryRoots.mockResolvedValue([]);
        scheduling.libraryTasks.mockResolvedValue([]);
        scheduling.schedules.mockResolvedValue([]);
        scheduling.addSchedule.mockResolvedValue({} as never);
        const { element: form, stable } = await panel(scheduling, ['tenant.libraries', 'library.scan']);
        const kind = form.querySelector<HTMLSelectElement>('.schedule-form select')!;
        kind.value = 'weekly';
        kind.dispatchEvent(new Event('change'));
        await stable();
        const boxes = form.querySelectorAll<HTMLInputElement>('.schedule-form input[type="checkbox"]');
        expect(boxes).toHaveLength(7);
        boxes[0].dispatchEvent(new Event('change'));
        form.querySelector('form.schedule-form')!.dispatchEvent(new Event('submit'));
        await stable();
        expect(scheduling.addSchedule).toHaveBeenCalledWith(5, expect.objectContaining({ kind: 'weekly', days: 0b111_1110, intervalMinutes: 0 }));
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
        const prompt = vi.spyOn(window, 'prompt').mockReturnValue('club');
        const { element, stable } = await render(TenantsSection, api, ['server.tenants']);

        expect(element.querySelectorAll('.row')[0].textContent).toContain('Working here');
        click(element, 'Work here');
        await stable();
        expect(api.switchTenant).toHaveBeenCalledWith(2);

        [...element.querySelectorAll<HTMLButtonElement>('button.danger')][0].click();
        await stable();
        expect(api.deleteTenant).not.toHaveBeenCalled();
        prompt.mockReturnValue('Home');
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
