import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryInfo, LibraryRootInfo, MissingBookItem, MissingBookPage, MissingRemoval, ScheduleInfo, ScheduleKind, TaskRunInfo } from '../core/models';
import { SessionService } from '../core/session.service';

const POLL_MS = 1500;

/**
 * A library's folders, scans and schedules (agents/docs/features/tasks.md): folders added and removed, a scan started
 * and followed while it runs, the last runs with what they did, schedules added and deleted.
 */
@Component({
    selector: 'lib-library-panel',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="library-panel">
            <h3 class="field-label">{{ 'libraries.folders' | t }}</h3>
            <ul class="rows" [attr.aria-label]="'libraries.folders' | t">
                @for (root of roots(); track root.id) {
                    <li class="row">
                        <span class="row-main">
                            <span class="row-title">{{ root.path }}</span>
                            @if (!root.exists) {
                                <span class="badge danger">{{ 'libraries.folderMissing' | t }}</span>
                            }
                        </span>
                        <button type="button" class="button danger" [title]="'libraries.removeFolderHint' | t: { path: root.path }" [disabled]="busy()"
                                (click)="removeRoot(root)">{{ 'libraries.removeFolder' | t }}</button>
                    </li>
                } @empty {
                    <li class="row muted">{{ 'libraries.noFolders' | t }}</li>
                }
            </ul>
            <form class="toolbar" novalidate (submit)="addRoot($event)">
                <input type="text" class="toolbar-search" [placeholder]="'libraries.folderPath' | t" [attr.aria-label]="'libraries.folderPath' | t"
                       [value]="path()" (input)="path.set(valueOf($event))">
                <button type="submit" class="button" [title]="'libraries.addFolderHint' | t" [disabled]="busy() || !path().trim()">{{ 'libraries.addFolder' | t }}</button>
            </form>

            @if (canScan()) {
                <h3 class="field-label">{{ 'tasks.scanning' | t }}</h3>
                <div class="toolbar">
                    @if (running(); as run) {
                        <button type="button" class="button" [title]="'tasks.cancelHint' | t" (click)="cancel(run)">{{ 'common.cancel' | t }}</button>
                        <span class="row-meta" role="status">
                            @if (run.waiting) {
                                {{ 'tasks.waiting' | t }}
                            } @else if (run.progress; as progress) {
                                {{ 'tasks.progress' | t: { files: progress.filesSeen, added: progress.added, changed: progress.changed, missing: progress.missing, removed: progress.removed } }}
                            } @else {
                                {{ 'tasks.starting' | t }}
                            }
                        </span>
                    } @else {
                        <button type="button" class="button primary" [title]="'tasks.scanHint' | t" [disabled]="busy() || roots().length === 0" (click)="scan()">{{ 'tasks.scan' | t }}</button>
                    }
                </div>
                <ul class="rows" [attr.aria-label]="'tasks.runs' | t">
                    @for (run of runs(); track run.id) {
                        <li class="row">
                            <span class="row-main">
                                <span class="row-title">{{ when(run.started) }} · {{ ('task.' + run.task) | t }}</span>
                                <span class="row-meta">
                                    <span class="badge" [class.danger]="run.outcome === 'failed' || run.outcome === 'interrupted'">{{ ('outcome.' + run.outcome) | t }}</span>
                                    @if (run.trigger === 'schedule') {
                                        {{ 'tasks.bySchedule' | t }}
                                    }
                                    @if (run.counts; as counts) {
                                        {{ 'tasks.counts' | t: { added: counts.added, changed: counts.changed, missing: counts.missing, returned: counts.returned, removed: counts.removed, unchanged: counts.unchanged } }}
                                    }
                                    @if (run.summary) {
                                        {{ run.summary }}
                                    }
                                </span>
                                @if (run.counts?.missingRoots?.length || run.counts?.problems?.length) {
                                    <details>
                                        <summary>{{ 'tasks.problems' | t: { count: (run.counts!.missingRoots.length + run.counts!.problems.length) } }}</summary>
                                        <ul class="problems">
                                            @for (root of run.counts!.missingRoots; track $index) {
                                                <li>{{ 'tasks.missingFolder' | t: { path: root } }}</li>
                                            }
                                            @for (problem of run.counts!.problems; track $index) {
                                                <li>{{ problem }}</li>
                                            }
                                        </ul>
                                    </details>
                                }
                            </span>
                        </li>
                    } @empty {
                        <li class="row muted">{{ 'tasks.noRuns' | t }}</li>
                    }
                </ul>

                @if (missing(); as page) {
                    @if (page.total > 0) {
                        <h3 class="field-label">{{ 'missing.title' | t }}</h3>
                        <p class="row-meta">
                            {{ 'missing.summary' | t: { total: page.total } }}
                            @if (page.ready > 0) {
                                {{ 'missing.readySummary' | t: { ready: page.ready, days: page.keepDays } }}
                            }
                        </p>
                        <ul class="rows" [attr.aria-label]="'missing.title' | t">
                            @for (book of missingItems(); track book.id) {
                                <li class="row">
                                    <span class="row-main">
                                        <span class="row-title">{{ book.name }}</span>
                                        <span class="row-meta">
                                            @if (book.ready) {
                                                <span class="badge danger">{{ 'missing.ready' | t }}</span>
                                            }
                                            {{ 'missing.since' | t: { time: when(book.missingSince) } }} · <span class="path">{{ book.path }}</span>
                                        </span>
                                    </span>
                                    <button type="button" class="button danger" [title]="'missing.removeHint' | t" [disabled]="busy()" (click)="removeMissing(book)">{{ 'missing.remove' | t }}</button>
                                </li>
                            }
                        </ul>
                        <div class="toolbar">
                            @if (page.next) {
                                <button type="button" class="button" [disabled]="busy()" (click)="moreMissing()">{{ 'missing.more' | t }}</button>
                            }
                            @if (page.ready > 0) {
                                <button type="button" class="button danger" [disabled]="busy() || running() !== null" (click)="removeReady(page)">{{ 'missing.removeReady' | t: { count: page.ready, days: page.keepDays } }}</button>
                            }
                            <button type="button" class="button danger" [disabled]="busy() || running() !== null" (click)="removeAllMissing(page)">{{ 'missing.removeAll' | t: { count: page.total } }}</button>
                        </div>
                    }
                }

                <h3 class="field-label">{{ 'schedules.title' | t }}</h3>
                <ul class="rows" [attr.aria-label]="'schedules.title' | t">
                    @for (schedule of schedules(); track schedule.id) {
                        <li class="row">
                            <span class="row-main">
                                <span class="row-title">{{ describe(schedule) }}</span>
                                @if (schedule.nextRun) {
                                    <span class="row-meta">{{ 'schedules.next' | t: { time: when(schedule.nextRun) } }}</span>
                                }
                            </span>
                            <button type="button" class="button danger" [title]="'schedules.deleteHint' | t" [disabled]="busy()" (click)="deleteSchedule(schedule)">{{ 'common.delete' | t }}</button>
                        </li>
                    } @empty {
                        <li class="row muted">{{ 'schedules.none' | t }}</li>
                    }
                </ul>
                <form class="toolbar schedule-form" novalidate (submit)="addSchedule($event)">
                    <select [attr.aria-label]="'schedules.kind' | t" [value]="kind()" (change)="kind.set($any(valueOf($event)))">
                        <option value="interval">{{ 'schedules.interval' | t }}</option>
                        <option value="weekly">{{ 'schedules.weekly' | t }}</option>
                    </select>
                    @if (kind() === 'interval') {
                        <label class="inline">{{ 'schedules.everyHours' | t }}
                            <input type="number" min="1" max="744" [value]="hours()" (input)="hours.set(+valueOf($event))">
                        </label>
                    } @else {
                        @for (day of dayNames; track $index) {
                            <label class="check day">
                                <input type="checkbox" [checked]="hasDay($index)" (change)="toggleDay($index)">
                                <span>{{ day }}</span>
                            </label>
                        }
                        <input type="time" [attr.aria-label]="'schedules.time' | t" [value]="time()" (input)="time.set(valueOf($event))">
                    }
                    <button type="submit" class="button" [title]="'schedules.addHint' | t" [disabled]="busy() || (kind() === 'weekly' && days() === 0)">{{ 'schedules.add' | t }}</button>
                </form>
            }
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
        </div>
    `,
})
export class LibraryPanel implements OnInit, OnDestroy {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);

    readonly library = input.required<LibraryInfo>();

    protected readonly roots = signal<LibraryRootInfo[]>([]);
    protected readonly runs = signal<TaskRunInfo[]>([]);
    protected readonly schedules = signal<ScheduleInfo[]>([]);
    /** The library's missing books: the first page's counts, and the rows loaded so far. */
    protected readonly missing = signal<MissingBookPage | null>(null);
    protected readonly missingItems = signal<MissingBookItem[]>([]);
    protected readonly path = signal('');
    protected readonly kind = signal<ScheduleKind>('interval');
    protected readonly hours = signal(24);
    protected readonly days = signal(0b111_1111);
    protected readonly time = signal('03:00');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly canScan = computed(() => this.session.can('library.scan'));
    protected readonly running = computed(() => this.runs().find(run => run.outcome === 'running') ?? null);
    protected readonly valueOf = valueOf;
    /** Sunday first, as the schedule's day bits. */
    protected readonly dayNames = [...Array(7).keys()].map(day => new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(new Date(2026, 10, 1 + day)));

    private timer: ReturnType<typeof setTimeout> | null = null;
    private destroyed = false;

    async ngOnInit(): Promise<void> {
        await this.run(async () => {
            this.roots.set(await this.api.libraryRoots(this.library().id));
            if (this.canScan()) {
                this.schedules.set(await this.api.schedules(this.library().id));
                await this.refreshRuns();
            }
        });
        // On its own: the panel is usable while the missing books load.
        if (this.canScan())
            void this.refreshMissing().catch(error => this.error.set(errorText(this.i18n, error)));
    }

    ngOnDestroy(): void {
        this.destroyed = true;
        if (this.timer)
            clearTimeout(this.timer);
    }

    protected async addRoot(event: Event): Promise<void> {
        event.preventDefault();
        await this.run(async () => {
            await this.api.addLibraryRoot(this.library().id, this.path().trim());
            this.path.set('');
            this.roots.set(await this.api.libraryRoots(this.library().id));
        });
    }

    protected async removeRoot(root: LibraryRootInfo): Promise<void> {
        if (!confirm(this.i18n.t('libraries.removeFolderConfirm', { path: root.path })))
            return;
        await this.run(async () => {
            await this.api.removeLibraryRoot(this.library().id, root.id);
            this.roots.set(this.roots().filter(r => r.id !== root.id));
            if (this.canScan())
                await this.refreshRuns();
        });
    }

    protected async scan(): Promise<void> {
        await this.run(async () => {
            await this.api.scanLibrary(this.library().id);
            await this.refreshRuns();
        });
    }

    protected async cancel(run: TaskRunInfo): Promise<void> {
        await this.run(async () => {
            await this.api.cancelTask(run.id);
            await this.refreshRuns();
        });
    }

    protected hasDay(day: number): boolean {
        return (this.days() & (1 << day)) !== 0;
    }

    protected toggleDay(day: number): void {
        this.days.set(this.days() ^ (1 << day));
    }

    protected async addSchedule(event: Event): Promise<void> {
        event.preventDefault();
        const [hour, minute] = this.time().split(':').map(Number);
        const start = new Date();
        if (this.kind() === 'weekly')
            start.setHours(hour || 0, minute || 0, 0, 0);
        await this.run(async () => {
            await this.api.addSchedule(this.library().id, {
                kind: this.kind(),
                start: start.toISOString(),
                intervalMinutes: this.kind() === 'interval' ? Math.max(1, this.hours()) * 60 : 0,
                days: this.kind() === 'weekly' ? this.days() : 0,
                enabled: true,
            });
            this.schedules.set(await this.api.schedules(this.library().id));
        });
    }

    protected async deleteSchedule(schedule: ScheduleInfo): Promise<void> {
        await this.run(async () => {
            await this.api.deleteSchedule(this.library().id, schedule.id);
            this.schedules.set(this.schedules().filter(s => s.id !== schedule.id));
        });
    }

    protected describe(schedule: ScheduleInfo): string {
        if (schedule.kind === 'interval')
            return this.i18n.t('schedules.everyHoursText', { hours: Math.round(schedule.intervalMinutes / 6) / 10 });
        const days = this.dayNames.filter((_, day) => (schedule.days & (1 << day)) !== 0).join(', ');
        const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(schedule.start));
        return this.i18n.t('schedules.weeklyText', { days, time });
    }

    /** Removing one missing book: only after the user accepts. */
    protected async removeMissing(book: MissingBookItem): Promise<void> {
        if (confirm(this.i18n.t('missing.confirmOne', { name: book.name })))
            await this.startRemoval({ bookIds: [book.id] });
    }

    protected async removeReady(page: MissingBookPage): Promise<void> {
        if (confirm(this.i18n.t('missing.confirmReady', { count: page.ready, days: page.keepDays })))
            await this.startRemoval({ ready: true });
    }

    protected async removeAllMissing(page: MissingBookPage): Promise<void> {
        if (confirm(this.i18n.t('missing.confirmAll', { count: page.total })))
            await this.startRemoval({ all: true });
    }

    protected async moreMissing(): Promise<void> {
        const next = this.missing()?.next;
        if (!next)
            return;
        await this.run(async () => {
            const page = await this.api.missingBooks(this.library().id, next);
            this.missing.update(current => current ? { ...current, next: page.next } : page);
            this.missingItems.update(items => [...items, ...page.items]);
        });
    }

    private async startRemoval(removal: MissingRemoval): Promise<void> {
        await this.run(async () => {
            await this.api.removeMissingBooks(this.library().id, removal);
            await this.refreshRuns();
        });
    }

    private async refreshMissing(): Promise<void> {
        const page = await this.api.missingBooks(this.library().id);
        if (this.destroyed)
            return;
        this.missing.set(page);
        this.missingItems.set(page.items);
    }

    protected when(utc: string): string {
        return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(utc));
    }

    /** The last runs; while one runs, asked again every {@link POLL_MS}. */
    private async refreshRuns(): Promise<void> {
        if (this.timer)
            clearTimeout(this.timer);
        this.timer = null;
        const runs = await this.api.libraryTasks(this.library().id);
        if (this.destroyed)
            return;
        const wasRunning = this.running() !== null;
        this.runs.set(runs);
        // A run that just ended (a scan, a removal) changes the missing books.
        if (wasRunning && !runs.some(run => run.outcome === 'running'))
            await this.refreshMissing();
        if (runs.some(run => run.outcome === 'running'))
            this.timer = setTimeout(() => void this.refreshRuns().catch(error => this.error.set(errorText(this.i18n, error))), POLL_MS);
    }

    private async run(work: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await work();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
