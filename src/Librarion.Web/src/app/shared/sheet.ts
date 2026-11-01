import { ChangeDetectionStrategy, Component, ElementRef, input, output, viewChild } from '@angular/core';
import { TranslatePipe } from '../core/i18n.service';
import { Icon } from './icon';

/**
 * A dialog card (port of SFE's `SheetComponent`): title, a scrolling body, a footer for buttons (elements marked
 * `sheetFooter`). A modal `<dialog>`: focus stays inside, Esc closes, focus returns where it was. On phones it rises
 * from the bottom as a sheet (styles/dialogs.css). `open()` shows it.
 */
@Component({
    selector: 'lib-sheet',
    imports: [TranslatePipe, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <dialog #dialog class="sheet" [class.wide]="wide()" [attr.aria-labelledby]="id + '-title'"
                (pointerdown)="onPointerDown($event)" (click)="backdropClick($event)" (close)="closed.emit()">
            <header class="sheet-head">
                <div class="sheet-title">
                    <h2 [id]="id + '-title'">{{ heading() }}</h2>
                    @if (subtitle()) {
                        <p class="sheet-subtitle">{{ subtitle() }}</p>
                    }
                </div>
                <button type="button" class="icon-button" [title]="'common.closeHint' | t" [attr.aria-label]="'common.close' | t" (click)="close()">
                    <svg libIcon="close"></svg>
                </button>
            </header>
            <div class="sheet-body">
                <ng-content />
            </div>
            <footer class="sheet-foot">
                <ng-content select="[sheetFooter]" />
            </footer>
        </dialog>
    `,
})
export class Sheet {
    private static next = 0;

    readonly heading = input.required<string>();
    readonly subtitle = input<string | null>(null);
    readonly wide = input(false);
    readonly closed = output<void>();

    protected readonly id = `sheet-${++Sheet.next}`;
    private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
    private pressedBackdrop = false;

    open(): void {
        const dialog = this.dialog().nativeElement;
        if (dialog.open)
            return;
        // jsdom has no showModal: the tests get an open dialog all the same.
        if (typeof dialog.showModal === 'function')
            dialog.showModal();
        else
            dialog.setAttribute('open', '');
    }

    close(): void {
        const dialog = this.dialog().nativeElement;
        if (typeof dialog.close === 'function') {
            dialog.close();
        } else if (dialog.hasAttribute('open')) {
            dialog.removeAttribute('open');
            dialog.dispatchEvent(new Event('close'));
        }
    }

    /** A method, not an assignment in the template: a handler returning false would cancel every press inside (SFE). */
    protected onPointerDown(event: PointerEvent): void {
        this.pressedBackdrop = event.target === this.dialog().nativeElement;
    }

    /** A click on the backdrop closes it, only when the press started there too (a shrinking card ends a click outside). */
    protected backdropClick(event: MouseEvent): void {
        if (event.target === this.dialog().nativeElement && this.pressedBackdrop)
            this.close();
        this.pressedBackdrop = false;
    }
}
