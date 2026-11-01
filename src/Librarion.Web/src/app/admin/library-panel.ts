import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ImportInput, LibraryInfo, LibraryRootInfo, MissingBookItem, MissingBookPage, MissingRemoval, TaskRunInfo } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { EmptyState } from '../shared/placeholders';
import { SchedulesTab } from './schedules-tab';

const POLL_MS = 1500;

/** The sub-pages of a library, in order. */
export const LIBRARY_TABS = ['folders', 'runs', 'import', 'missing', 'schedules'] as const;
export type LibraryTab = typeof LIBRARY_TABS[number];

/**
 * One sub-page of a library (`tab`; agents/docs/features/tasks.md): its folders added and removed, the last runs with
 * what they did, an import from another source (agents/docs/features/imports.md), its missing books, its schedules
 * (`SchedulesTab`).
 * Above every tab, the scan bar: a scan started, or the run going on followed with its progress. The data loads once
 * for all tabs (the component stays while the tab changes), and runs are polled while one goes.
 */
@Component({
    selector: 'lib-library-panel',
    imports: [TranslatePipe, EmptyState, SchedulesTab],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="library-panel">
            @if (canScan()) {
                <div class="toolbar scan-bar">
                    @if (running(); as run) {
                        <button type="button" class="button" [title]="'tasks.cancelHint' | t" (click)="cancel(run)">{{ 'common.cancel' | t }}</button>
                        <span class="row-meta" role="status">
                            @if (run.waiting) {
                                {{ 'tasks.waiting' | t }}
                            } @else if (run.progress; as progress) {
                                @if (run.task === 'import') {
                                    {{ 'tasks.importProgress' | t: { records: progress.filesSeen, added: progress.added, changed: progress.changed, skipped: progress.skipped } }}
                                } @else {
                                    {{ 'tasks.progress' | t: { files: progress.filesSeen, added: progress.added, changed: progress.changed, missing: progress.missing, removed: progress.removed } }}
                                }
                            } @else {
                                {{ 'tasks.starting' | t }}
                            }
                        </span>
                    } @else {
                        <button type="button" class="button primary" [title]="'tasks.scanHint' | t" [disabled]="busy() || roots().length === 0" (click)="scan()">{{ 'tasks.scan' | t }}</button>
                    }
                </div>
            }
            @switch (tab()) {
                @case ('folders') {
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
                            <li class="row" libEmpty="folder">{{ 'libraries.noFolders' | t }}</li>
                        }
                    </ul>
                    <form class="toolbar" novalidate (submit)="addRoot($event)">
                        <input type="text" class="toolbar-search" [placeholder]="'libraries.folderPath' | t" [attr.aria-label]="'libraries.folderPath' | t"
                               [value]="path()" (input)="path.set(valueOf($event))">
                        <button type="submit" class="button" [title]="'libraries.addFolderHint' | t" [disabled]="busy() || !path().trim()">{{ 'libraries.addFolder' | t }}</button>
                    </form>
                }
                @case ('runs') {
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
                                            @if (run.task === 'import') {
                                                {{ 'tasks.importCounts' | t: { added: counts.added, changed: counts.changed, unchanged: counts.unchanged, skipped: counts.skipped } }}
                                            } @else {
                                                {{ 'tasks.counts' | t: { added: counts.added, changed: counts.changed, missing: counts.missing, returned: counts.returned, removed: counts.removed, unchanged: counts.unchanged } }}
                                            }
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
                            <li class="row" libEmpty="activity">{{ 'tasks.noRuns' | t }}</li>
                        }
                    </ul>
                }
                @case ('import') {
                    @if (importKinds().length > 0) {
                        <p class="row-meta">{{ 'import.explain' | t }}</p>
                        <form class="import-form" novalidate (submit)="startImport($event)">
                            <select [attr.aria-label]="'import.kind' | t" [value]="importKind()" (change)="importKind.set(valueOf($event))">
                                @for (key of importKinds(); track key) {
                                    <option [value]="key">{{ ('importKind.' + key) | t }}</option>
                                }
                            </select>
                            <input type="text" [placeholder]="('importKind.' + importKind() + '.source') | t" [attr.aria-label]="'import.source' | t"
                                   [value]="importSource()" (input)="importSource.set(valueOf($event))">
                            <input type="text" [placeholder]="'import.files' | t" [attr.aria-label]="'import.files' | t" [title]="'import.filesHint' | t"
                                   [value]="importFiles()" (input)="importFiles.set(valueOf($event))">
                            <button type="submit" class="button primary" [title]="'import.startHint' | t" [disabled]="busy() || running() !== null || !importSource().trim()">{{ 'import.start' | t }}</button>
                        </form>
                    } @else {
                        <p class="empty" libEmpty="import">{{ 'import.noKinds' | t }}</p>
                    }
                }
                @case ('missing') {
                    @if (missing(); as page) {
                        @if (page.total > 0) {
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
                        } @else {
                            <p class="empty" libEmpty="missing">{{ 'missing.none' | t }}</p>
                        }
                    }
                }
                @case ('schedules') {
                    <lib-schedules-tab [library]="library()" [importKinds]="canImport() ? importKinds() : []" />
                }
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
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly session = inject(SessionService);

    readonly library = input.required<LibraryInfo>();
    readonly tab = input<LibraryTab>('folders');

    protected readonly roots = signal<LibraryRootInfo[]>([]);
    protected readonly runs = signal<TaskRunInfo[]>([]);
    /** The library's missing books: the first page's counts, and the rows loaded so far. */
    protected readonly missing = signal<MissingBookPage | null>(null);
    protected readonly missingItems = signal<MissingBookItem[]>([]);
    protected readonly path = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly canScan = computed(() => this.session.can(Rights.scan));
    protected readonly canImport = computed(() => this.session.can(Rights.import));
    /** The kinds of source the server can import from (`calibre`, `inpx`), and the form's values. */
    protected readonly importKinds = signal<string[]>([]);
    protected readonly importKind = signal('calibre');
    protected readonly importSource = signal('');
    protected readonly importFiles = signal('');
    protected readonly running = computed(() => this.runs().find(run => run.outcome === 'running') ?? null);
    protected readonly valueOf = valueOf;

    private timer: ReturnType<typeof setTimeout> | null = null;
    private destroyed = false;

    async ngOnInit(): Promise<void> {
        await this.run(async () => {
            this.roots.set(await this.api.libraryRoots(this.library().id));
            if (this.canScan()) {
                await this.refreshRuns();
            }
        });
        // On its own: the panel is usable while the missing books load.
        if (this.canScan())
            void this.refreshMissing().catch(error => this.error.set(errorText(this.i18n, error)));
        if (this.canScan() && this.canImport())
            void this.loadImportKinds().catch(error => this.error.set(errorText(this.i18n, error)));
    }

    private async loadImportKinds(): Promise<void> {
        const kinds = await this.api.importKinds(this.library().id);
        if (this.destroyed)
            return;
        this.importKinds.set(kinds);
        if (!kinds.includes(this.importKind()) && kinds.length > 0)
            this.importKind.set(kinds[0]);
    }

    /** An import from another source: nothing in the source is changed, and no book is removed, so no confirmation. */
    protected async startImport(event: Event): Promise<void> {
        event.preventDefault();
        const input: ImportInput = { kind: this.importKind(), source: this.importSource().trim() };
        if (this.importFiles().trim())
            input.files = this.importFiles().trim();
        await this.run(async () => {
            await this.api.importIntoLibrary(this.library().id, input);
            this.toasts.show(this.i18n.t('tasks.importStarted'));
            this.importSource.set('');
            this.importFiles.set('');
            this.roots.set(await this.api.libraryRoots(this.library().id));
            await this.refreshRuns();
        });
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
        const ok = await this.confirm.ask({
            title: this.i18n.t('libraries.removeFolderTitle'), message: this.i18n.t('libraries.removeFolderConfirm', { path: root.path }),
            action: this.i18n.t('common.remove'), danger: true,
        });
        if (!ok)
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
            this.toasts.show(this.i18n.t('tasks.scanStarted'));
            await this.refreshRuns();
        });
    }

    protected async cancel(run: TaskRunInfo): Promise<void> {
        await this.run(async () => {
            await this.api.cancelTask(run.id);
            await this.refreshRuns();
        });
    }

    /** Removing one missing book: only after the user accepts. */
    protected async removeMissing(book: MissingBookItem): Promise<void> {
        if (await this.askRemoval(this.i18n.t('missing.confirmOne', { name: book.name })))
            await this.startRemoval({ bookIds: [book.id] });
    }

    protected async removeReady(page: MissingBookPage): Promise<void> {
        if (await this.askRemoval(this.i18n.t('missing.confirmReady', { count: page.ready, days: page.keepDays })))
            await this.startRemoval({ ready: true });
    }

    protected async removeAllMissing(page: MissingBookPage): Promise<void> {
        if (await this.askRemoval(this.i18n.t('missing.confirmAll', { count: page.total })))
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

    private askRemoval(message: string): Promise<boolean> {
        return this.confirm.ask({ title: this.i18n.t('missing.confirmTitle'), message, action: this.i18n.t('common.remove'), danger: true });
    }

    private async startRemoval(removal: MissingRemoval): Promise<void> {
        await this.run(async () => {
            await this.api.removeMissingBooks(this.library().id, removal);
            this.toasts.show(this.i18n.t('missing.removalStarted'));
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
