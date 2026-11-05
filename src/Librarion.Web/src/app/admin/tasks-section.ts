import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { LibraryInfo, ScheduleInfo, TaskOverview, TaskRunInfo } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { formatMoment, formatRelative, runDetail, runElapsed } from '../core/task-text';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { Icon, IconName } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { RunList } from './run-list';
import { SchedulesTab } from './schedules-tab';

/** Refreshed this often while the Tasks page shows (and the page is visible). */
const REFRESH_MS = 2000;

type CardKind = 'libraryScan' | 'import';
const ICONS: Record<CardKind, IconName> = { libraryScan: 'refresh', import: 'import' };

/**
 * Administration › Background tasks (SFE's TasksSectionComponent; agents/docs/features/task-progress.md), one sub-page
 * each: Tasks (a summary line, then a card per kind: its state, the runs going on with their progress, the last run, the
 * next one planned, Run now), Schedules (every library's, each with its own list and dialog), History (every library's
 * runs, a page at a time). Covers the libraries the session may scan (`api/tasks/overview`).
 */
@Component({
    selector: 'lib-tasks-section',
    imports: [TranslatePipe, Icon, RouterLink, RunList, SchedulesTab, EmptyState, Skeleton],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @switch (page()) {
            @case ('tasks') {
                @if (overview(); as overview) {
                    <p class="task-summary" aria-live="polite">{{ summary() }}</p>
                    <div class="task-cards">
                        @for (kind of kinds(); track kind) {
                            @let running = runningOf(kind);
                            @let last = lastOf(kind);
                            @let next = nextOf(kind);
                            <article class="task-card" [attr.data-state]="running.length ? 'running' : 'idle'" [attr.aria-labelledby]="'task-card-' + kind">
                                <header class="task-card-head">
                                    <span class="task-icon" aria-hidden="true"><svg [libIcon]="icons[kind]"></svg></span>
                                    <div class="task-card-title">
                                        <h2 [id]="'task-card-' + kind">{{ ('taskCard.' + kind) | t }}</h2>
                                        <span class="task-state" [attr.data-state]="running.length ? 'running' : 'idle'">
                                            @if (running.length) {
                                                <span class="spinner" aria-hidden="true"></span>
                                                {{ 'taskCard.running' | t }} · {{ elapsed(running[0]) }}
                                            } @else {
                                                {{ 'taskCard.idle' | t }}
                                            }
                                        </span>
                                    </div>
                                    <div class="task-card-actions">
                                        @if (scannable().length > 1) {
                                            <select [attr.aria-label]="'taskCard.library' | t" [title]="'taskCard.libraryHint' | t" (change)="choose(kind, $event)">
                                                @for (library of scannable(); track library.id) {
                                                    <option [value]="library.id" [selected]="library.id === chosen(kind)?.id">{{ library.name }}</option>
                                                }
                                            </select>
                                        }
                                        @if (chosen(kind); as library) {
                                            @if (kind === 'libraryScan') {
                                                <button type="button" class="button" [title]="'taskCard.scanHint' | t: { name: library.name }"
                                                        [disabled]="busy() || runningIn(library.id)" (click)="scan(library)">
                                                    <svg libIcon="refresh"></svg>{{ 'taskCard.runNow' | t }}
                                                </button>
                                            } @else {
                                                <a class="button" [routerLink]="['/admin/libraries', library.id, 'import']" [title]="'taskCard.importHint' | t: { name: library.name }">
                                                    <svg libIcon="import"></svg>{{ 'taskCard.startImport' | t }}
                                                </a>
                                            }
                                        }
                                        <a class="button" routerLink="/admin/tasks/schedules" [title]="'taskCard.scheduleHint' | t"><svg libIcon="clock"></svg>{{ 'taskCard.schedule' | t }}</a>
                                    </div>
                                </header>
                                <p class="task-purpose">{{ ('taskCard.' + kind + '.hint') | t }}</p>
                                @for (run of running; track run.id) {
                                    <div class="task-running">
                                        <div class="task-progress" aria-hidden="true"><span></span></div>
                                        <p class="task-card-detail"><strong>{{ watch.libraryName(run) }}:</strong> {{ detail(run) }}</p>
                                        @if (run.progress?.current; as current) {
                                            <p class="task-current" [title]="current">{{ current }}</p>
                                        }
                                        <button type="button" class="button danger" [title]="'taskPanel.cancelHint' | t" (click)="cancel(run)">{{ 'common.cancel' | t }}</button>
                                    </div>
                                }
                                <dl class="task-times">
                                    <div>
                                        <dt>{{ 'taskCard.lastRun' | t }}</dt>
                                        <dd>
                                            @if (last) {
                                                <a routerLink="/admin/tasks/history" [title]="'taskCard.lastRunHint' | t">
                                                    <span class="badge" [class.danger]="last.outcome === 'failed' || last.outcome === 'interrupted'">{{ ('outcome.' + last.outcome) | t }}</span>
                                                    <time [attr.datetime]="last.started">{{ moment(last.started) }}</time>
                                                </a>
                                                <span class="field-hint">{{ watch.libraryName(last) }}</span>
                                            } @else {
                                                <span class="field-hint">{{ 'taskCard.never' | t }}</span>
                                            }
                                        </dd>
                                    </div>
                                    <div class="task-next" [class.planned]="next">
                                        <dt>{{ 'taskCard.nextRun' | t }}</dt>
                                        <dd>
                                            @if (next) {
                                                <time [attr.datetime]="next.nextRun">{{ moment(next.nextRun!) }}</time>
                                                <span class="task-relative">{{ relative(next.nextRun!) }}</span>
                                                <span class="field-hint">{{ 'taskCard.bySchedule' | t: { name: libraryOf(next.libraryId) } }}</span>
                                            } @else {
                                                <span class="field-hint">{{ 'taskCard.notPlanned' | t }}</span>
                                            }
                                        </dd>
                                    </div>
                                </dl>
                            </article>
                        }
                    </div>
                } @else if (!error()) {
                    <lib-skeleton [rows]="4" />
                }
            }
            @case ('schedules') {
                <p class="row-meta">{{ 'tasksSection.schedulesIntro' | t }}</p>
                @for (library of scannable(); track library.id) {
                    <section class="tasks-library" [attr.aria-labelledby]="'tasks-library-' + library.id">
                        <h2 class="group-label" [id]="'tasks-library-' + library.id">{{ library.name }}</h2>
                        <lib-schedules-tab [library]="library" [importKinds]="importKinds()" />
                    </section>
                } @empty {
                    @if (overview()) {
                        <p class="empty" libEmpty="clock">{{ 'tasksSection.noLibraries' | t }}</p>
                    } @else if (!error()) {
                        <lib-skeleton [rows]="3" />
                    }
                }
            }
            @case ('history') {
                @if (history(); as runs) {
                    <lib-run-list [runs]="runs" [libraryName]="nameOf" />
                    @if (more()) {
                        <div class="toolbar">
                            <button type="button" class="button" [disabled]="busy()" (click)="older()">{{ 'tasksSection.older' | t }}</button>
                        </div>
                    }
                } @else if (!error()) {
                    <lib-skeleton [rows]="5" />
                }
            }
        }
    `,
})
export class TasksSection {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);
    private readonly confirm = inject(ConfirmService);
    private readonly session = inject(SessionService);
    private readonly libraries = inject(LibraryContext);
    protected readonly watch = inject(TaskWatch);

    /** The sub-page: `tasks`, `schedules` or `history`. */
    readonly page = input.required<string>();

    protected readonly icons = ICONS;
    protected readonly overview = signal<TaskOverview | null>(null);
    protected readonly history = signal<TaskRunInfo[] | null>(null);
    protected readonly more = signal(false);
    protected readonly importKinds = signal<string[]>([]);
    protected readonly error = signal<string | null>(null);
    protected readonly busy = signal(false);
    protected readonly now = signal(Date.now());
    private asked = 0;
    /** The library each card acts on (the first one by default). */
    private readonly picked = signal<Partial<Record<CardKind, number>>>({});

    protected readonly kinds = computed<CardKind[]>(() => this.session.can(Rights.import) ? ['libraryScan', 'import'] : ['libraryScan']);
    /** The libraries the session may scan, in the switcher's order. */
    protected readonly scannable = computed<LibraryInfo[]>(() => {
        const ids = new Set(this.overview()?.libraries ?? []);
        return this.libraries.libraries().filter(library => ids.has(library.id));
    });

    /** "Running: scan of Comics · next: import into Fiction 03/11 03:00", or "Nothing running · nothing planned". */
    protected readonly summary = computed(() => {
        const overview = this.overview();
        if (!overview)
            return '';
        const running = overview.running.map(run => this.i18n.t('tasksSection.runningItem', { task: this.i18n.t(`task.${run.task}`), name: this.watch.libraryName(run) }));
        const next = this.planned(overview.schedules)[0];
        return [
            running.length ? this.i18n.t('tasksSection.running', { list: running.join(', ') }) : this.i18n.t('tasksSection.idle'),
            next
                ? this.i18n.t('tasksSection.next', { task: this.i18n.t(`task.${next.task}`), name: this.libraryOf(next.libraryId), time: this.moment(next.nextRun!) })
                : this.i18n.t('tasksSection.nothingPlanned'),
        ].join(' · ');
    });

    protected readonly nameOf = (run: TaskRunInfo) => this.watch.libraryName(run);

    constructor() {
        effect(onCleanup => {
            const page = this.page();
            untracked(() => {
                if (page === 'history')
                    void this.loadHistory();
                else
                    void this.refresh();
                if (page === 'schedules')
                    void this.loadImportKinds();
            });
            if (page !== 'tasks')
                return;
            const timer = setInterval(() => {
                if (!document.hidden)
                    void this.refresh();
            }, REFRESH_MS);
            onCleanup(() => clearInterval(timer));
        });
        // A run ended or started elsewhere (the panel's watch): the cards follow.
        effect(() => {
            this.watch.running();
            if (untracked(this.page) === 'tasks')
                untracked(() => void this.refresh());
        });
    }

    protected runningOf(kind: CardKind): TaskRunInfo[] {
        return this.overview()?.running.filter(run => run.task === kind) ?? [];
    }

    protected lastOf(kind: CardKind): TaskRunInfo | null {
        return this.overview()?.last.find(run => run.task === kind) ?? null;
    }

    /** The next run planned for the kind: the enabled schedule coming due first. */
    protected nextOf(kind: CardKind): ScheduleInfo | null {
        return this.planned(this.overview()?.schedules ?? []).find(schedule => schedule.task === kind) ?? null;
    }

    protected runningIn(library: number): boolean {
        return this.overview()?.running.some(run => run.libraryId === library) ?? false;
    }

    protected chosen(kind: CardKind): LibraryInfo | null {
        const libraries = this.scannable();
        return libraries.find(library => library.id === this.picked()[kind]) ?? libraries[0] ?? null;
    }

    protected choose(kind: CardKind, event: Event): void {
        this.picked.update(picked => ({ ...picked, [kind]: Number(valueOf(event)) }));
    }

    protected libraryOf(id: number): string {
        return this.libraries.libraries().find(library => library.id === id)?.name ?? this.i18n.t('taskRun.library', { id });
    }

    protected detail(run: TaskRunInfo): string {
        return runDetail(this.i18n, run);
    }

    protected elapsed(run: TaskRunInfo): string {
        return runElapsed(run, this.now());
    }

    protected moment(utc: string): string {
        return formatMoment(utc);
    }

    protected relative(utc: string): string {
        return formatRelative(utc, this.now(), this.i18n.language());
    }

    protected async scan(library: LibraryInfo): Promise<void> {
        await this.act(async () => {
            const { runId } = await this.api.scanLibrary(library.id);
            this.toasts.show(this.i18n.t('tasks.scanStarted'));
            this.watch.follow(runId);
            await this.refresh();
        });
    }

    protected async cancel(run: TaskRunInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('taskPanel.cancelTitle'), message: this.i18n.t('taskPanel.cancelMessage'), action: this.i18n.t('taskPanel.cancelAction'), danger: true,
        });
        if (ok)
            await this.act(async () => {
                await this.watch.cancel(run);
                await this.refresh();
            });
    }

    protected async older(): Promise<void> {
        const runs = this.history() ?? [];
        await this.act(async () => {
            const page = await this.api.taskHistory(runs[runs.length - 1]?.id);
            this.libraries.knows(page.map(run => run.libraryId));
            this.history.set([...runs, ...page]);
            this.more.set(page.length === 20);
        });
    }

    private planned(schedules: ScheduleInfo[]): ScheduleInfo[] {
        return schedules.filter(schedule => schedule.enabled && schedule.nextRun)
            .sort((a, b) => Date.parse(a.nextRun!) - Date.parse(b.nextRun!));
    }

    private async refresh(): Promise<void> {
        // An answer overtaken by a later question (the 2 s refresh against one after Run now) is dropped.
        const asked = ++this.asked;
        try {
            const overview = await this.api.taskOverview();
            if (asked !== this.asked)
                return;
            this.libraries.knows([...overview.libraries, ...overview.last.map(run => run.libraryId)]);
            this.overview.set(overview);
            this.error.set(null);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
        this.now.set(Date.now());
    }

    private async loadHistory(): Promise<void> {
        try {
            const page = await this.api.taskHistory();
            this.libraries.knows(page.map(run => run.libraryId));
            this.history.set(page);
            this.more.set(page.length === 20);
            this.error.set(null);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    private async loadImportKinds(): Promise<void> {
        const first = this.scannable()[0] ?? (await this.refresh(), this.scannable()[0]);
        if (!first || !this.session.can(Rights.import))
            return;
        this.importKinds.set(await this.api.importKinds(first.id).catch(() => []));
    }

    private async act(work: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        try {
            await work();
        } catch (error) {
            this.toasts.error(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
