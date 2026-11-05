import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal } from '@angular/core';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { TaskRunInfo } from '../core/models';
import { runDetail, runElapsed, runTitle } from '../core/task-text';
import { TaskWatch } from '../core/task-watch';
import { ConfirmService } from './confirm';
import { Icon, IconName } from './icon';

const ICONS: Record<TaskRunInfo['task'], IconName> = {
    libraryScan: 'refresh', import: 'import', removeRoot: 'folder', removeMissing: 'missing', tagDelete: 'trash', tagMerge: 'tag', fieldMerge: 'category', mergeUndo: 'back', tagCounts: 'refresh', scopeTags: 'tag',
};

/**
 * The runs going on, under the top bar (SFE's scan panel; agents/docs/features/task-progress.md): a thin moving bar,
 * "Scanning Comics…", the time it has taken, the counts so far and the file being read, Cancel; once ended its result
 * until dismissed. Its height is `--task-panel-h` for what sticks below it (the administration's column).
 */
@Component({
    selector: 'lib-task-panel',
    imports: [TranslatePipe, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'task-panel', 'aria-live': 'polite', '[hidden]': '!runs().length' },
    template: `
        @for (run of runs(); track run.id) {
            @let running = run.outcome === 'running';
            <div class="task-row" [class.running]="running" [class.failed]="run.outcome === 'failed' || run.outcome === 'interrupted'">
                @if (running) {
                    <div class="task-bar" aria-hidden="true"><span></span></div>
                }
                <svg [libIcon]="icons[run.task]" class="task-row-icon"></svg>
                <div class="task-text">
                    <strong class="task-title">{{ title(run) }}</strong>
                    @if (!running) {
                        <span class="badge" [class.danger]="run.outcome === 'failed' || run.outcome === 'interrupted'">{{ ('outcome.' + run.outcome) | t }}</span>
                    }
                    <span class="task-elapsed" [title]="'taskPanel.elapsed' | t">{{ elapsed(run) }}</span>
                    <span class="task-detail">{{ detail(run) }}</span>
                    @if (running && run.progress?.current; as current) {
                        <span class="task-current" [title]="current">{{ current }}</span>
                    }
                </div>
                @if (running) {
                    <button type="button" class="button" [title]="'taskPanel.cancelHint' | t" (click)="cancel(run)">{{ 'common.cancel' | t }}</button>
                } @else {
                    <button type="button" class="icon-button" [attr.aria-label]="'taskPanel.dismiss' | t" [title]="'taskPanel.dismissHint' | t" (click)="watch.dismiss(run)">
                        <svg libIcon="close"></svg>
                    </button>
                }
            </div>
        }
    `,
})
export class TaskPanel {
    protected readonly watch = inject(TaskWatch);
    private readonly i18n = inject(I18n);
    private readonly confirm = inject(ConfirmService);
    protected readonly icons = ICONS;

    protected readonly runs = computed(() => [...this.watch.running(), ...this.watch.ended()]);
    /** Ticks the elapsed times each second while something runs. */
    private readonly now = signal(Date.now());

    constructor() {
        const destroyRef = inject(DestroyRef);
        destroyRef.onDestroy(this.watch.watch());
        effect(onCleanup => {
            if (!this.watch.running().length)
                return;
            const timer = setInterval(() => {
                if (!document.hidden)
                    this.now.set(Date.now());
            }, 1000);
            onCleanup(() => clearInterval(timer));
        });

        // Its height, for what sticks under it (0 while hidden).
        if (typeof ResizeObserver !== 'undefined') {
            const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
            const root = document.documentElement.style;
            const observer = new ResizeObserver(() => root.setProperty('--task-panel-h', `${host.offsetHeight}px`));
            observer.observe(host);
            destroyRef.onDestroy(() => {
                observer.disconnect();
                root.removeProperty('--task-panel-h');
            });
        }
    }

    protected title(run: TaskRunInfo): string {
        return runTitle(this.i18n, run, this.watch.libraryName(run));
    }

    protected detail(run: TaskRunInfo): string {
        return runDetail(this.i18n, run);
    }

    protected elapsed(run: TaskRunInfo): string {
        return runElapsed(run, this.now());
    }

    protected async cancel(run: TaskRunInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('taskPanel.cancelTitle'), message: this.i18n.t('taskPanel.cancelMessage'), action: this.i18n.t('taskPanel.cancelAction'), danger: true,
        });
        if (ok)
            await this.watch.cancel(run);
    }
}
