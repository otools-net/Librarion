import { ChangeDetectionStrategy, Component, Injectable, computed, effect, inject, signal, viewChild } from '@angular/core';
import { valueOf } from '../core/errors';
import { TranslatePipe } from '../core/i18n.service';
import { Sheet } from './sheet';

export interface ConfirmRequest {
    title: string;
    message: string;
    /** The text of the button that goes ahead ("Delete", "Remove"). */
    action: string;
    /** Destroys something: the button is red and Cancel has the focus. */
    danger?: boolean;
    /** The button stays off until this is typed (deleting a tenant: its name). */
    typeToConfirm?: string;
}

interface Pending extends ConfirmRequest {
    resolve: (ok: boolean) => void;
}

/** Asks before an action, in a dialog of the page (`ConfirmDialog`, once in the app) instead of the browser's `confirm()`. */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
    private readonly current = signal<Pending | null>(null);
    readonly pending = this.current.asReadonly();

    /** True when the user goes ahead; false on Cancel, Esc or a click outside. A new question cancels the open one. */
    ask(request: ConfirmRequest): Promise<boolean> {
        this.current()?.resolve(false);
        return new Promise(resolve => this.current.set({ ...request, resolve }));
    }

    answer(ok: boolean): void {
        const pending = this.current();
        if (!pending)
            return;
        this.current.set(null);
        pending.resolve(ok);
    }
}

/** The dialog of `ConfirmService`. */
@Component({
    selector: 'lib-confirm-dialog',
    imports: [TranslatePipe, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (service.pending(); as request) {
            <lib-sheet [heading]="request.title" (closed)="closed()">
                <p class="confirm-message">{{ request.message }}</p>
                @if (request.typeToConfirm) {
                    <label class="field">
                        <span class="field-label">{{ 'confirm.typeName' | t: { name: request.typeToConfirm } }}</span>
                        <input type="text" autocomplete="off" autofocus [value]="typed()" (input)="typed.set(valueOf($event))">
                    </label>
                }
                <div sheetFooter class="sheet-actions">
                    <button type="button" class="button" [title]="'common.cancelHint' | t" [attr.autofocus]="request.typeToConfirm ? null : ''" (click)="finish(false)">{{ 'common.cancel' | t }}</button>
                    <button type="button" class="button" [class.danger]="request.danger" [class.primary]="!request.danger"
                            [disabled]="!allowed()" (click)="finish(true)">{{ request.action }}</button>
                </div>
            </lib-sheet>
        }
    `,
})
export class ConfirmDialog {
    protected readonly service = inject(ConfirmService);
    private readonly sheet = viewChild(Sheet);
    protected readonly typed = signal('');
    protected readonly allowed = computed(() => {
        const expected = this.service.pending()?.typeToConfirm;
        return !expected || this.typed().trim() === expected;
    });
    private result = false;

    protected readonly valueOf = valueOf;

    constructor() {
        // Follows the question too: a new one may come before the dialog of the last one leaves the page.
        effect(() => {
            const request = this.service.pending();
            const sheet = this.sheet();
            if (request && sheet) {
                this.result = false;
                this.typed.set('');
                sheet.open();
            }
        });
    }

    /** Closes the dialog (focus goes back where it was), then answers. */
    protected finish(ok: boolean): void {
        this.result = ok && this.allowed();
        const sheet = this.sheet();
        if (sheet)
            sheet.close();
        else
            this.closed();
    }

    protected closed(): void {
        this.service.answer(this.result);
    }
}
