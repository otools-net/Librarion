import { ChangeDetectionStrategy, Component, ElementRef, effect, inject } from '@angular/core';
import { TranslatePipe } from '../core/i18n.service';
import { ToastService } from '../core/toast.service';
import { Icon } from './icon';

/** The toasts of `ToastService`, in the corner of the page (at the bottom on phones) and above open dialogs. */
@Component({
    selector: 'lib-toasts',
    imports: [TranslatePipe, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: {
        class: 'toasts', role: 'status', 'aria-live': 'polite', popover: 'manual',
        '(pointerenter)': 'service.hold()', '(pointerleave)': 'service.release()',
        '(focusin)': 'service.hold()', '(focusout)': 'service.release()',
    },
    template: `
        @for (toast of service.toasts(); track toast.id) {
            <div class="toast" [class.error]="toast.error">
                <svg [libIcon]="toast.error ? 'alert' : 'check'"></svg>
                <span class="toast-text">{{ toast.message }}</span>
                <button type="button" class="icon-button" [title]="'common.close' | t" [attr.aria-label]="'common.close' | t" (click)="service.dismiss(toast.id)">
                    <svg libIcon="close"></svg>
                </button>
            </div>
        }
    `,
})
export class Toasts {
    protected readonly service = inject(ToastService);

    constructor() {
        const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
        let count = 0;
        // Modal dialogs sit in the top layer, above any z-index: showing the box again as a popover puts it above them.
        effect(() => {
            const shown = this.service.toasts().length;
            if (shown > count && typeof host.showPopover === 'function') {
                if (host.matches(':popover-open'))
                    host.hidePopover();
                host.showPopover();
            }
            count = shown;
        });
    }
}
