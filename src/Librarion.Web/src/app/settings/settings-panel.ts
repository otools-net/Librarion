import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SettingLevel, SettingState } from '../core/models';
import { ToastService } from '../core/toast.service';
import { Skeleton } from '../shared/placeholders';

/**
 * The settings of one level (as SFE's SettingsPanel): each key with its value, where an inherited value comes from,
 * and, above the user level, a lock for the levels below. Saves only what changed, all or nothing. Texts:
 * `setting.<key>`, `setting.<key>.<choice>`, `settingSection.<section>`.
 */
@Component({
    selector: 'lib-settings-panel',
    imports: [TranslatePipe, Skeleton],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (states(); as states) {
            <form class="settings-form" novalidate (submit)="save($event)">
                @for (state of states; track state.key) {
                    <div class="setting-row" [class.changed]="edits()[state.key] !== undefined">
                        <label class="field-label" [for]="'setting-' + state.key">{{ 'setting.' + state.key | t }}</label>
                        <div class="setting-control">
                            @if (state.kind === 'choice') {
                                <select [id]="'setting-' + state.key" [disabled]="!state.editable" (change)="edit(state, valueOf($event))">
                                    @for (choice of state.choices; track choice) {
                                        <option [value]="choice" [selected]="current(state) === choice">{{ 'setting.' + state.key + '.' + choice | t }}</option>
                                    }
                                </select>
                            } @else {
                                <input type="number" [id]="'setting-' + state.key" [min]="state.min" [max]="state.max" [disabled]="!state.editable"
                                       [value]="current(state)" (input)="edit(state, toNumber(valueOf($event)))">
                            }
                            @if (level() !== 'user') {
                                <label class="check" [title]="'settings.lockHint' | t">
                                    <input type="checkbox" [checked]="locked(state)" [disabled]="!state.editable" (change)="lock(state, checkedOf($event))">
                                    <span>{{ 'settings.lock' | t }}</span>
                                </label>
                            }
                        </div>
                        <span class="field-hint">
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
                @if (error(); as error) {
                    <p class="form-error" role="alert">{{ error }}</p>
                }
                <div class="form-actions">
                    <button type="submit" class="button primary" [title]="'settings.saveHint' | t" [disabled]="busy() || !changed()">{{ 'common.save' | t }}</button>
                    <button type="button" class="button" [title]="'settings.discardHint' | t" [disabled]="busy() || !changed()" (click)="discard()">{{ 'common.discard' | t }}</button>
                </div>
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
    protected readonly changed = computed(() => Object.keys(this.edits()).length > 0 || Object.keys(this.locks()).length > 0);

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    constructor() {
        effect(() => {
            const level = this.level();
            const owner = this.owner();
            untracked(() => void this.load(level, owner));
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
