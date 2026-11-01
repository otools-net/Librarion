import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SettingLevel, SettingState } from '../core/models';
import { ToastService } from '../core/toast.service';
import { UnsavedChanges } from '../core/unsaved-changes';
import { SettingReveal } from './setting-reveal';
import { Skeleton } from '../shared/placeholders';

/**
 * The settings of one level (as SFE's SettingsPanel): in groups by section, each key with its hint, its value, where
 * an inherited value comes from, and, above the user level, a lock for the levels below. Changes are marked; the save
 * bar (sticky at the bottom while there are any) saves only what changed, all or nothing; leaving with changes asks
 * first (`UnsavedChanges`). Texts: `setting.<key>` (+ `.hint`, `.<choice>`), `settingSection.<section>`.
 */
@Component({
    selector: 'lib-settings-panel',
    imports: [TranslatePipe, Skeleton],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (states(); as states) {
            <form class="settings-form" novalidate (submit)="save($event)">
                @for (group of groups(); track group.section) {
                    <section class="settings-group" [attr.aria-labelledby]="'settings-group-' + group.section">
                        <h2 class="group-label" [id]="'settings-group-' + group.section">{{ 'settingSection.' + group.section | t }}</h2>
                        @for (state of group.states; track state.key) {
                            @let changed = edits()[state.key] !== undefined || locks()[state.key] !== undefined;
                            <div class="setting-row" [class.changed]="changed" [id]="'setting-row-' + state.key">
                                <label class="field-label" [for]="'setting-' + state.key">
                                    {{ 'setting.' + state.key | t }}
                                    @if (changed) {
                                        <span class="changed-mark" [title]="'settings.changedMark' | t"><span class="visually-hidden">{{ 'settings.changedMark' | t }}</span></span>
                                    }
                                </label>
                                <span class="field-hint" [id]="'setting-hint-' + state.key">{{ 'setting.' + state.key + '.hint' | t }}</span>
                                <div class="setting-control">
                                    @if (state.kind === 'choice') {
                                        <select [id]="'setting-' + state.key" [disabled]="!state.editable" [attr.aria-describedby]="'setting-hint-' + state.key + ' setting-source-' + state.key"
                                                [attr.aria-invalid]="errors()[state.key] ? 'true' : null" (change)="edit(state, valueOf($event))">
                                            @for (choice of state.choices; track choice) {
                                                <option [value]="choice" [selected]="current(state) === choice">{{ 'setting.' + state.key + '.' + choice | t }}</option>
                                            }
                                        </select>
                                    } @else {
                                        <input type="number" [id]="'setting-' + state.key" [min]="state.min" [max]="state.max" [disabled]="!state.editable"
                                               [attr.aria-describedby]="'setting-hint-' + state.key + ' setting-source-' + state.key"
                                               [attr.aria-invalid]="errors()[state.key] ? 'true' : null" [value]="current(state)" (input)="edit(state, toNumber(valueOf($event)))">
                                    }
                                    @if (level() !== 'user') {
                                        <label class="check" [title]="'settings.lockHint' | t">
                                            <input type="checkbox" [checked]="locked(state)" [disabled]="!state.editable" (change)="lock(state, checkedOf($event))">
                                            <span>{{ 'settings.lock' | t }}</span>
                                        </label>
                                    }
                                </div>
                                <span class="field-hint setting-source" [id]="'setting-source-' + state.key">
                                    @if (state.lockedAbove) {
                                        {{ 'settings.lockedAbove' | t }}
                                    } @else if (state.source === level()) {
                                        {{ 'settings.ownValue' | t }}
                                    } @else {
                                        {{ 'settings.inherited' | t: { from: ('settingLevel.' + (state.source ?? 'default')) | t } }}
                                    }
                                </span>
                                @if (errors()[state.key]; as error) {
                                    <span class="field-error">{{ error }}</span>
                                }
                            </div>
                        }
                    </section>
                }
                @if (error(); as error) {
                    <p class="form-error" role="alert">{{ error }}</p>
                }
                @if (changed()) {
                    <div class="save-bar" role="region" [attr.aria-label]="'settings.saveBar' | t">
                        <span class="save-bar-status" role="status">{{ 'settings.unsaved' | t: { count: changeCount() } }}</span>
                        <button type="button" class="button" [title]="'settings.discardHint' | t" [disabled]="busy()" (click)="discard()">{{ 'common.discard' | t }}</button>
                        <button type="submit" class="button primary" [title]="'settings.saveHint' | t" [disabled]="busy()">{{ 'common.save' | t }}</button>
                    </div>
                }
            </form>
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        } @else {
            <lib-skeleton shape="form" [rows]="6" />
        }
    `,
})
export class SettingsPanel {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);
    private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

    readonly level = input.required<SettingLevel>();
    /** The library (or another tenant) whose settings these are; none = the session's own. */
    readonly owner = input<number | undefined>(undefined);
    /** After a save, with the new states (the account page applies the preferences). */
    readonly savedStates = output<SettingState[]>();

    protected readonly states = signal<SettingState[] | null>(null);
    protected readonly edits = signal<Record<string, unknown>>({});
    protected readonly locks = signal<Record<string, boolean>>({});
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly changeCount = computed(() => new Set([...Object.keys(this.edits()), ...Object.keys(this.locks())]).size);
    protected readonly changed = computed(() => this.changeCount() > 0);
    /** The settings by section, in the order the server lists them. */
    protected readonly groups = computed(() => {
        const groups: { section: string; states: SettingState[] }[] = [];
        for (const state of this.states() ?? []) {
            let group = groups.find(g => g.section === state.section);
            if (!group)
                groups.push(group = { section: state.section, states: [] });
            group.states.push(state);
        }
        return groups;
    });

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    constructor() {
        const unsaved = inject(UnsavedChanges);
        effect(() => unsaved.set(this, this.changed(), () => this.discard()));
        inject(DestroyRef).onDestroy(() => unsaved.set(this, false, () => { }));
        effect(() => {
            const level = this.level();
            const owner = this.owner();
            untracked(() => void this.load(level, owner));
        });
        // A setting picked in the administration's search, once this level's settings are shown.
        const reveal = inject(SettingReveal);
        const injector = inject(Injector);
        effect(() => {
            const pending = reveal.pending();
            const states = this.states();
            if (pending && pending.level === this.level() && states?.some(s => s.key === pending.key))
                afterNextRender(() => this.reveal(pending.key, reveal), { injector });
        });
    }

    protected current(state: SettingState): unknown {
        const edits = this.edits();
        return state.key in edits ? edits[state.key] : state.value;
    }

    protected locked(state: SettingState): boolean {
        return this.locks()[state.key] ?? state.locked;
    }

    protected edit(state: SettingState, value: unknown): void {
        this.edits.update(edits => {
            const next = { ...edits };
            if (value === state.value)
                delete next[state.key];
            else
                next[state.key] = value;
            return next;
        });
    }

    protected lock(state: SettingState, locked: boolean): void {
        this.locks.update(locks => {
            const next = { ...locks };
            if (locked === state.locked)
                delete next[state.key];
            else
                next[state.key] = locked;
            return next;
        });
    }

    protected toNumber(text: string): number | string {
        const number = Number(text);
        return text.trim() !== '' && Number.isInteger(number) ? number : text;
    }

    protected discard(): void {
        this.edits.set({});
        this.locks.set({});
        this.errors.set({});
        this.error.set(null);
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            const locks = Object.entries(this.locks());
            const states = await this.api.saveSettings(this.level(), {
                values: this.edits(),
                lock: locks.filter(([, locked]) => locked).map(([key]) => key),
                unlock: locks.filter(([, locked]) => !locked).map(([key]) => key),
            }, this.owner());
            this.states.set(states);
            this.discard();
            this.toasts.show(this.i18n.t('common.saved'));
            this.savedStates.emit(states);
        } catch (error) {
            const byField = fieldErrors(this.i18n, error);
            if (Object.keys(byField).length)
                this.errors.set(byField);
            else
                this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    /** Scrolls to the setting `key`, focuses its control and flashes its row (again when found again). */
    private reveal(key: string, reveal: SettingReveal): void {
        const row = this.host.querySelector<HTMLElement>(`#setting-row-${CSS.escape(key)}`);
        if (!row || reveal.pending()?.key !== key)
            return;
        reveal.pending.set(null);
        row.scrollIntoView?.({ block: 'center' });
        this.host.querySelector<HTMLElement>(`#setting-${CSS.escape(key)}`)?.focus({ preventScroll: true });
        row.classList.remove('setting-found');
        void row.offsetWidth;
        row.classList.add('setting-found');
        row.addEventListener('animationend', () => row.classList.remove('setting-found'), { once: true });
    }

    private async load(level: SettingLevel, owner: number | undefined): Promise<void> {
        this.states.set(null);
        this.discard();
        try {
            this.states.set(await this.api.settings(level, owner));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }
}
