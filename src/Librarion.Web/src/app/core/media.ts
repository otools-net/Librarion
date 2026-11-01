import { Injectable, signal } from '@angular/core';

/** Phones: the width where the navigation and the administration change shape (styles: `max-width: 640px`). */
export const PHONE_QUERY = '(max-width: 640px)';

/** Whether the screen is phone-sized, followed as the window changes (jsdom has no `matchMedia`: wide). */
@Injectable({ providedIn: 'root' })
export class Media {
    private readonly query = typeof matchMedia === 'function' ? matchMedia(PHONE_QUERY) : null;
    private readonly phoneWidth = signal(this.query?.matches ?? false);
    readonly phone = this.phoneWidth.asReadonly();

    constructor() {
        this.query?.addEventListener('change', event => this.phoneWidth.set(event.matches));
    }
}
