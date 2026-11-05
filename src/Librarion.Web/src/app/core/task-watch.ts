import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { I18n } from './i18n.service';
import { LibraryContext } from './library-context';
import { TaskRunInfo } from './models';
import { readPreference, writePreference } from './preferences';
import { Rights } from './rights';
import { SessionService } from './session.service';
import { runTitle } from './task-text';
import { ToastService } from './toast.service';

/** While a run goes: asked again this often. */
export const RUNNING_POLL_MS = 1500;
/** Otherwise, to see a run started elsewhere (a schedule, another person): only while the page is visible. */
export const IDLE_POLL_MS = 20000;
const DISMISSED = 'librarion.tasks.dismissed';

/**
 * The runs going on in the libraries the session may scan (as SFE's ScanService; agents/docs/features/task-progress.md),
 * followed for the progress panel: `running` (with their progress), then, once ended, in `ended` until dismissed
 * (remembered per browser), with a toast saying so. Polls only while watched, often while something runs, rarely
 * otherwise, never while the page is hidden; the server answers from memory when nothing runs.
 */
@Injectable({ providedIn: 'root' })
export class TaskWatch {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);
    private readonly session = inject(SessionService);
    private readonly libraries = inject(LibraryContext);

    readonly running = signal<TaskRunInfo[]>([]);
    readonly ended = signal<TaskRunInfo[]>([]);
    // Tag runs (merges, deletes) belong to no library: those who manage tags follow them too.
    private readonly enabled = computed(() => this.session.session()?.user != null && (this.session.can(Rights.scan) || this.session.can(Rights.manageTags)));

    private watchers = 0;
    private timer: ReturnType<typeof setTimeout> | null = null;
    private asking = false;
    /** A refresh was asked for while one was on its way: another follows it. */
    private again = false;
    private readonly followed = new Set<number>();
    private dismissed = new Set<number>(readDismissed());

    constructor() {
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden && this.watchers > 0)
                void this.refresh();
        });
    }

    /** Starts following (the panel, while shown); the function returned stops. */
    watch(): () => void {
        if (this.watchers++ === 0)
            void this.refresh();
        return () => {
            if (--this.watchers === 0)
                this.stop();
        };
    }

    /**
     * A run started from this page: its result shows even when it ends before a poll sees it running (a small
     * library's scan takes less than a second).
     */
    follow(runId: number): void {
        this.followed.add(runId);
        void this.refresh();
    }

    /** Asks now (a run was just started here, or cancelled). */
    async refresh(): Promise<void> {
        this.stop();
        if (!this.enabled()) {
            this.running.set([]);
            return;
        }
        if (this.asking) {
            this.again = true;
            return;
        }
        this.asking = true;
        try {
            if (!document.hidden) {
                const before = new Set([...this.running().map(run => run.id), ...this.followed]);
                this.followed.clear();
                const now = await this.api.runningTasks();
                this.libraries.knows(now.map(run => run.libraryId));
                this.running.set(now);
                for (const gone of [...before].filter(id => !now.some(run => run.id === id)))
                    await this.finished(gone);
            }
        } catch {
            // A hiccup (the server restarting): asked again later.
        } finally {
            this.asking = false;
        }
        if (this.again) {
            this.again = false;
            return this.refresh();
        }
        if (this.watchers > 0)
            this.timer = setTimeout(() => void this.refresh(), this.running().length || this.followed.size ? RUNNING_POLL_MS : IDLE_POLL_MS);
    }

    async cancel(run: TaskRunInfo): Promise<void> {
        await this.api.cancelTask(run.id);
        await this.refresh();
    }

    dismiss(run: TaskRunInfo): void {
        this.dismissed.add(run.id);
        writePreference(DISMISSED, JSON.stringify([...this.dismissed].slice(-20)));
        this.ended.update(runs => runs.filter(each => each.id !== run.id));
    }

    /** The name of a run's library, as the switcher has it. */
    libraryName(run: TaskRunInfo): string {
        if (run.libraryId === null)
            return this.i18n.t('taskRun.everyLibrary');
        return this.libraries.libraries().find(library => library.id === run.libraryId)?.name ?? this.i18n.t('taskRun.library', { id: run.libraryId ?? 0 });
    }

    /** A run that was going is over: its result, kept until dismissed, and a toast. */
    private async finished(runId: number): Promise<void> {
        const done = await this.api.task(runId).catch(() => null);
        if (!done)
            return;
        // Its end not written yet: asked again with the next poll.
        if (done.outcome === 'running') {
            this.followed.add(runId);
            return;
        }
        if (this.dismissed.has(done.id))
            return;
        this.ended.update(runs => [...runs.filter(each => each.id !== done.id), done]);
        const text = `${runTitle(this.i18n, done, this.libraryName(done))}: ${this.i18n.t(`outcome.${done.outcome}`)}`;
        if (done.outcome === 'succeeded')
            this.toasts.show(text);
        else
            this.toasts.error(text);
        // The libraries' book counts changed.
        void this.libraries.load();
    }

    private stop(): void {
        if (this.timer)
            clearTimeout(this.timer);
        this.timer = null;
    }
}

function readDismissed(): number[] {
    try {
        const ids = JSON.parse(readPreference(DISMISSED) ?? '[]');
        return Array.isArray(ids) ? ids.filter((id): id is number => typeof id === 'number') : [];
    } catch {
        return [];
    }
}
