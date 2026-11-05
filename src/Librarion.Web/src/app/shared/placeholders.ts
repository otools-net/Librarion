import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MessageKey, TranslatePipe } from '../core/i18n.service';
import { Icon, IconName } from './icon';

/**
 * An empty list or page: an icon and what to do next, never a bare "nothing". `<li class="row" libEmpty="folder">…</li>`
 * in a list of rows; with the class `large`, centred on a page (an empty library, a search without results).
 */
@Component({
    selector: '[libEmpty]',
    imports: [Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'empty' },
    template: '<svg [libIcon]="libEmpty()"></svg><span class="empty-text"><ng-content /></span>',
})
export class EmptyState {
    readonly libEmpty = input.required<IconName>();
}

/**
 * Grey bars in the shape of what is loading (rows, a form), shimmering unless motion is reduced; screen readers hear
 * the label ("Loading…") once.
 */
@Component({
    selector: 'lib-skeleton',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'skeleton', role: 'status', 'aria-busy': 'true', '[class.skeleton-form]': 'shape() === "form"' },
    template: `
        <span class="visually-hidden">{{ label() | t }}</span>
        @for (line of lines(); track line) {
            <span class="skeleton-line" aria-hidden="true"></span>
        }
    `,
})
export class Skeleton {
    readonly rows = input(3);
    readonly shape = input<'rows' | 'form'>('rows');
    readonly label = input<MessageKey>('common.loading');
    protected readonly lines = computed(() => Array.from({ length: this.rows() }, (_, i) => i));
}
