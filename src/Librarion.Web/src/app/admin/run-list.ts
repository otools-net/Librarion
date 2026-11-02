import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { TaskRunInfo } from '../core/models';
import { formatMoment, runDetail, runElapsed } from '../core/task-text';
import { EmptyState } from '../shared/placeholders';

/**
 * Task runs as rows, newest first (a library's Runs tab; every library's on Background tasks › History): when, what,
 * its outcome, by schedule, what it did, how long, its problems folded; `libraryName` adds the library, linking to its
 * runs.
 */
@Component({
    selector: 'lib-run-list',
    imports: [TranslatePipe, EmptyState, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <ul class="rows" [attr.aria-label]="'tasks.runs' | t">
            @for (run of runs(); track run.id) {
                <li class="row">
                    <span class="row-main">
                        <span class="row-title">
                            {{ when(run.started) }} · {{ ('task.' + run.task) | t }}
                            @if (libraryName(); as name) {
                                · <a [routerLink]="['/admin/libraries', run.libraryId, 'runs']" [title]="'tasks.libraryRuns' | t: { name: name(run) }">{{ name(run) }}</a>
                            }
                        </span>
                        <span class="row-meta">
                            <span class="badge" [class.danger]="run.outcome === 'failed' || run.outcome === 'interrupted'">{{ ('outcome.' + run.outcome) | t }}</span>
                            @if (run.trigger === 'schedule') {
                                {{ 'tasks.bySchedule' | t }}
                            }
                            {{ detail(run) }}
                            @if (run.finished) {
                                <span class="run-took" [title]="'tasks.took' | t">{{ took(run) }}</span>
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
    `,
})
export class RunList {
    private readonly i18n = inject(I18n);

    readonly runs = input.required<TaskRunInfo[]>();
    /** The library of a run, when the rows mix libraries. */
    readonly libraryName = input<((run: TaskRunInfo) => string) | null>(null);

    protected when(utc: string): string {
        return formatMoment(utc);
    }

    protected detail(run: TaskRunInfo): string {
        return runDetail(this.i18n, run);
    }

    protected took(run: TaskRunInfo): string {
        return runElapsed(run, Date.now());
    }
}
