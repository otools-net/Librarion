import { Injectable, computed, inject, signal } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { ConfirmService } from '../shared/confirm';
import { I18n } from './i18n.service';

/**
 * Forms with changes not saved yet (the settings forms; SFE's SettingsForm marks): their sections are marked, leaving
 * the page asks first (`leaveWithUnsaved`), and closing the tab gets the browser's question (`beforeunload`).
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChanges {
    private readonly forms = signal(new Map<object, () => void>());
    readonly dirty = computed(() => this.forms().size > 0);

    constructor() {
        addEventListener('beforeunload', event => {
            if (this.dirty())
                event.preventDefault();
        });
    }

    /** `form` has changes (`discard` forgets them) or has none any more. */
    set(form: object, dirty: boolean, discard: () => void): void {
        this.forms.update(forms => {
            if (dirty === forms.has(form))
                return forms;
            const next = new Map(forms);
            if (dirty)
                next.set(form, discard);
            else
                next.delete(form);
            return next;
        });
    }

    /** Asks before changes are lost; true when there are none, or the user lets them go (they are discarded). */
    async confirmLeave(confirm: ConfirmService, i18n: I18n): Promise<boolean> {
        if (!this.dirty())
            return true;
        const ok = await confirm.ask({
            title: i18n.t('unsaved.title'), message: i18n.t('unsaved.message'), action: i18n.t('unsaved.leave'), danger: true,
        });
        if (ok) {
            const discards = [...this.forms().values()];
            this.forms.set(new Map());
            discards.forEach(discard => discard());
        }
        return ok;
    }
}

/**
 * The administration's guard: moving to another section, sub-page or library, or out of it, with unsaved changes
 * asks first. (Its route is reused between sections: guards run when its parameters change.)
 */
export const leaveWithUnsaved: CanDeactivateFn<unknown> = () =>
    inject(UnsavedChanges).confirmLeave(inject(ConfirmService), inject(I18n));
