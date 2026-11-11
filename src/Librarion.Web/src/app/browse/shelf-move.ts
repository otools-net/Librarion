import { ChangeDetectionStrategy, Component, effect, inject, input, output, viewChild } from '@angular/core';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { ShelfMove } from './shelf-order';

/**
 * Move on a shelf (agents/docs/features/shelves.md): what a drag does, with buttons (a click on a book's grip opens
 * it; WCAG 2.5.7): to the start, one place up or down, to the end; a book or the selection it is in. It stays open
 * while moving one place at a time, its place read out.
 */
@Component({
    selector: 'lib-shelf-move',
    imports: [TranslatePipe, Icon, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="heading()" (closed)="closed.emit()">
            <p class="shelf-move-place" role="status">{{ 'shelfMove.place' | t: { place: i18n.number(place()) } }}</p>
            <div class="shelf-move-buttons">
                <button type="button" class="button" [title]="'shelfMove.startHint' | t" [disabled]="busy() || place() === 1" (click)="move.emit('start')">
                    <svg libIcon="chevron-up"></svg>{{ 'shelfMove.start' | t }}
                </button>
                <button type="button" class="button" [title]="'shelfMove.upHint' | t" [disabled]="busy() || place() === 1" (click)="move.emit('up')">
                    <svg libIcon="chevron-left"></svg>{{ 'shelfMove.up' | t }}
                </button>
                <button type="button" class="button" [title]="'shelfMove.downHint' | t" [disabled]="busy() || last()" (click)="move.emit('down')">
                    <svg libIcon="chevron-right"></svg>{{ 'shelfMove.down' | t }}
                </button>
                <button type="button" class="button" [title]="'shelfMove.endHint' | t" [disabled]="busy() || last()" (click)="move.emit('end')">
                    <svg libIcon="chevron-down"></svg>{{ 'shelfMove.end' | t }}
                </button>
            </div>
            <p class="field-hint">{{ 'shelfMove.hint' | t }}</p>
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button primary" [title]="'common.closeHint' | t" (click)="sheet()?.close()">{{ 'shelfPick.done' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class ShelfMoveDialog {
    protected readonly i18n = inject(I18n);

    readonly heading = input.required<string>();
    /** The place of the (first) book moved in the shelf, from 1. */
    readonly place = input.required<number>();
    /** Nothing comes after it. */
    readonly last = input(false);
    readonly busy = input(false);
    readonly move = output<ShelfMove>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);

    constructor() {
        effect(() => this.sheet()?.open());
    }
}
