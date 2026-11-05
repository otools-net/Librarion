import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { I18n } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { ScheduleInfo, TaskOverview, TaskRunInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { formatElapsed, runDetail, runTitle } from '../core/task-text';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { TaskPanel } from '../shared/task-panel';
import { TasksSection } from './tasks-section';

const run = (overrides: Partial<TaskRunInfo> = {}): TaskRunInfo => ({
    id: 7, libraryId: 5, task: 'libraryScan', trigger: 'manual', started: new Date(Date.now() - 74_000).toISOString(), finished: null, outcome: 'running',
    summary: null, counts: null, progress: { filesSeen: 120, archivesDone: 1, added: 40, changed: 2, removed: 0, current: 'big.zip/a.fb2', missing: 1, returned: 0, skipped: 0 },
    waiting: false, ...overrides,
});

const counts = { added: 3, changed: 0, removed: 0, unchanged: 9, archivesRead: 1, archivesSkipped: 0, unreadable: 0, missingRoots: [], problems: [], missing: 0, returned: 0, skipped: 0 };

const schedule = (overrides: Partial<ScheduleInfo> = {}): ScheduleInfo => ({
    id: 1, libraryId: 6, task: 'import', importKind: 'calibre', source: '/srv/calibre/metadata.db', files: '/srv/calibre', kind: 'interval',
    start: '2026-11-01T03:00:00Z', intervalMinutes: 1440, days: 0, enabled: true, lastRun: null, nextRun: new Date(Date.now() + 5 * 3600_000).toISOString(), ...overrides,
});

function setUp(api: ApiMock, rights = ['library.scan', 'library.import']) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    localStorage.clear();
    TestBed.inject(SessionService).set(sessionInfo(rights));
    api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Comics', created: '' }, { id: 6, tenantId: 1, name: 'Fiction', created: '' }]);
    return TestBed.inject(LibraryContext).load();
}

describe('task-text', () => {
    it('words a run: its title, what it did so far or did, and the time it took', () => {
        TestBed.resetTestingModule();
        const i18n = TestBed.inject(I18n);
        expect(runTitle(i18n, run(), 'Comics')).toBe('Scanning Comics…');
        expect(runTitle(i18n, run({ task: 'import' }), 'Comics')).toBe('Importing into Comics…');
        expect(runTitle(i18n, run({ outcome: 'succeeded' }), 'Comics')).toBe('Scan · Comics');
        expect(runDetail(i18n, run())).toBe('120 files seen · 40 added · 2 changed · 1 missing · 0 removed');
        expect(runDetail(i18n, run({ waiting: true }))).toBe('Waiting for another scan to end…');
        expect(runDetail(i18n, run({ outcome: 'failed', progress: null, summary: 'The task stopped on an error.' }))).toBe('The task stopped on an error.');
        expect(runDetail(i18n, run({ outcome: 'succeeded', counts }))).toContain('3 added');
        expect([formatElapsed(42), formatElapsed(725), formatElapsed(3723)]).toEqual(['0:42', '12:05', '1:02:03']);
    });
});

describe('TaskPanel', () => {
    async function render(api: ApiMock) {
        await setUp(api);
        const fixture = TestBed.createComponent(TaskPanel);
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const stable = async () => { await settle(); await fixture.whenStable(); };
        return { fixture, element: fixture.nativeElement as HTMLElement, stable };
    }

    it('stays hidden while nothing runs', async () => {
        const api = mockApi();
        api.runningTasks.mockResolvedValue([]);
        const { element } = await render(api);
        expect(api.runningTasks).toHaveBeenCalledTimes(1);
        expect(element.hidden).toBe(true);
    });

    it('shows a run going on: title, time, counts, the file being read, Cancel asking first', async () => {
        const api = mockApi();
        api.runningTasks.mockResolvedValue([run()]);
        api.cancelTask.mockResolvedValue(undefined);
        const { element, stable } = await render(api);

        expect(element.hidden).toBe(false);
        expect(element.querySelector('.task-title')?.textContent).toBe('Scanning Comics…');
        expect(element.querySelector('.task-elapsed')?.textContent).toMatch(/^1:1\d$/);
        expect(element.querySelector('.task-detail')?.textContent).toContain('120 files seen');
        expect(element.querySelector('.task-current')?.textContent).toBe('big.zip/a.fb2');
        expect(element.querySelector('.task-bar')).not.toBeNull();

        const ask = vi.spyOn(TestBed.inject(ConfirmService), 'ask').mockResolvedValue(false);
        element.querySelector<HTMLButtonElement>('.task-row button')!.click();
        await stable();
        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ title: 'Stop this task?', danger: true }));
        expect(api.cancelTask).not.toHaveBeenCalled();
        ask.mockResolvedValue(true);
        element.querySelector<HTMLButtonElement>('.task-row button')!.click();
        await stable();
        expect(api.cancelTask).toHaveBeenCalledWith(7);
    });

    it('keeps the result of a run that ended until dismissed, says so in a toast, and remembers the dismissal', async () => {
        const api = mockApi();
        api.runningTasks.mockResolvedValue([run()]);
        api.task.mockResolvedValue(run({ outcome: 'succeeded', finished: new Date().toISOString(), progress: null, counts }));
        const { element, stable } = await render(api);
        const toast = vi.spyOn(TestBed.inject(ToastService), 'show');

        api.runningTasks.mockResolvedValue([]);
        await TestBed.inject(TaskWatch).refresh();
        await stable();
        expect(element.querySelector('.task-title')?.textContent).toBe('Scan · Comics');
        expect(element.querySelector('.task-row .badge')?.textContent).toBe('done');
        expect(element.querySelector('.task-detail')?.textContent).toContain('3 added');
        expect(element.querySelector('.task-bar')).toBeNull();
        expect(toast).toHaveBeenCalledWith('Scan · Comics: done');

        element.querySelector<HTMLButtonElement>('.task-row .icon-button')!.click();
        await stable();
        expect(element.hidden).toBe(true);
        expect(JSON.parse(localStorage.getItem('librarion.tasks.dismissed')!)).toEqual([7]);
    });

    it('shows the result of a run started here even when it ended before a poll saw it running', async () => {
        const api = mockApi();
        api.runningTasks.mockResolvedValue([]);
        api.task.mockResolvedValue(run({ id: 9, outcome: 'succeeded', finished: new Date().toISOString(), progress: null, counts }));
        const { element, stable } = await render(api);
        expect(element.hidden).toBe(true);

        TestBed.inject(TaskWatch).follow(9);
        await stable();
        expect(api.task).toHaveBeenCalledWith(9);
        expect(element.querySelector('.task-title')?.textContent).toBe('Scan · Comics');
    });

    it('asks again for a run gone from the running ones whose end is not written yet', async () => {
        const api = mockApi();
        api.runningTasks.mockResolvedValue([]);
        api.task.mockResolvedValueOnce(run({ id: 9 }));
        api.task.mockResolvedValueOnce(run({ id: 9, outcome: 'cancelled', finished: new Date().toISOString(), progress: null }));
        const { element, stable } = await render(api);
        const watch = TestBed.inject(TaskWatch);

        watch.follow(9);
        await stable();
        expect(element.hidden).toBe(true);
        await watch.refresh();
        await stable();
        expect(api.task).toHaveBeenCalledTimes(2);
        expect(element.querySelector('.task-row .badge')?.textContent).toBe('cancelled');
    });

    it('asks nothing of a session that may scan nowhere', async () => {
        const api = mockApi();
        await setUp(api, []);
        TestBed.createComponent(TaskPanel);
        await settle();
        expect(api.runningTasks).not.toHaveBeenCalled();
    });
});

describe('TasksSection', () => {
    async function render(api: ApiMock, page: string) {
        await setUp(api);
        api.runningTasks.mockResolvedValue([]);
        const fixture = TestBed.createComponent(TasksSection);
        fixture.componentRef.setInput('page', page);
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const stable = async () => { await settle(); await fixture.whenStable(); };
        return { fixture, element: fixture.nativeElement as HTMLElement, stable };
    }

    const overview = (overrides: Partial<TaskOverview> = {}): TaskOverview => ({
        running: [run()], last: [run({ id: 3, task: 'import', libraryId: 6, outcome: 'failed', finished: '2026-11-01T10:00:00Z', progress: null })],
        schedules: [schedule()], libraries: [5, 6], ...overrides,
    });

    it('shows a card per kind: its state, the run going on, the last run and the next one planned', async () => {
        const api = mockApi();
        api.taskOverview.mockResolvedValue(overview());
        const { element } = await render(api, 'tasks');

        expect(element.querySelector('.task-summary')?.textContent).toMatch(/^Running: Scan in Comics · next: Import from another source in Fiction /);
        const cards = [...element.querySelectorAll('.task-card')];
        expect(cards.map(card => card.querySelector('h2')?.textContent)).toEqual(['Folder scan', 'Import']);
        expect(cards[0].getAttribute('data-state')).toBe('running');
        expect(cards[0].querySelector('.task-state')?.textContent).toMatch(/Running · 1:1\d/);
        expect(cards[0].querySelector('.task-card-detail')?.textContent).toContain('Comics: 120 files seen');
        expect(cards[0].querySelector('.task-times')?.textContent).toContain('Not run yet');
        expect(cards[0].querySelector('.task-next')?.textContent).toContain('Not planned');
        expect(cards[1].getAttribute('data-state')).toBe('idle');
        expect(cards[1].querySelector('.task-times .badge')?.textContent).toBe('failed');
        expect(cards[1].querySelector('.task-next .task-relative')?.textContent).toBe('in 5 hours');
        expect(cards[1].querySelector('.task-next')?.textContent).toContain('scheduled in Fiction');
    });

    it('scans the library chosen on the card, not one already running', async () => {
        const api = mockApi();
        api.taskOverview.mockResolvedValue(overview());
        api.scanLibrary.mockResolvedValue({ runId: 9, started: true });
        const { element, stable } = await render(api, 'tasks');
        const card = element.querySelector('.task-card')!;
        const runNow = () => [...card.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === 'Run now')!;

        expect(runNow().disabled).toBe(true);
        const select = card.querySelector<HTMLSelectElement>('select')!;
        select.value = '6';
        select.dispatchEvent(new Event('change'));
        await stable();
        expect(runNow().disabled).toBe(false);
        runNow().click();
        await stable();
        expect(api.scanLibrary).toHaveBeenCalledWith(6);
    });

    it('lists every library\'s runs with their library, older ones on request', async () => {
        const api = mockApi();
        api.taskHistory.mockResolvedValueOnce(Array.from({ length: 20 }, (_, i) => run({ id: 40 - i, outcome: 'succeeded', counts, libraryId: i % 2 ? 5 : 6 })));
        api.taskHistory.mockResolvedValueOnce([run({ id: 2, outcome: 'cancelled' })]);
        const { element, stable } = await render(api, 'history');

        expect(element.querySelectorAll('.row')).toHaveLength(20);
        expect(element.querySelector('.row-title a')?.textContent).toBe('Fiction');
        [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === 'Older runs')!.click();
        await stable();
        expect(api.taskHistory).toHaveBeenLastCalledWith(21);
        expect(element.querySelectorAll('.row')).toHaveLength(21);
        expect(element.textContent).not.toContain('Older runs');
    });

    it('lists the schedules of each library the session may scan', async () => {
        const api = mockApi();
        api.taskOverview.mockResolvedValue(overview({ libraries: [6] }));
        api.schedules.mockResolvedValue([schedule()]);
        api.importKinds.mockResolvedValue(['calibre', 'inpx']);
        const { element } = await render(api, 'schedules');

        expect([...element.querySelectorAll('.tasks-library h2')].map(h => h.textContent)).toEqual(['Fiction']);
        expect(api.schedules).toHaveBeenCalledWith(6);
        expect(element.querySelector('lib-schedules-tab')?.textContent).toContain('Calibre');
    });
});
