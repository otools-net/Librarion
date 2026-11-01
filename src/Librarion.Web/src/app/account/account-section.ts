import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SettingState } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ThemeService } from '../core/theme.service';
import { SettingsPanel } from '../settings/settings-panel';

/** My account in the administration (SFE's AccountSection): own preferences, or the password of a local account. */
@Component({
    selector: 'lib-account-section',
    imports: [TranslatePipe, SettingsPanel],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (sessions.user(); as user) {
            @if (page() === 'password' && !user.local) {
                <p class="notice">{{ 'account.notLocal' | t }}</p>
            } @else if (page() === 'password' && sessions.can(rights.ownPassword)) {
                <section class="panel">
                    <form class="settings-form" novalidate (submit)="changePassword($event)">
                        <div class="field">
                            <label class="field-label" for="current-password">{{ 'account.currentPassword' | t }}</label>
                            <input id="current-password" type="password" autocomplete="current-password" [value]="current()" (input)="current.set(valueOf($event))">
                            @if (errors()['current']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="new-password">{{ 'signIn.newPassword' | t }}</label>
                            <input id="new-password" type="password" autocomplete="new-password" [value]="password()" (input)="password.set(valueOf($event))">
                            @if (errors()['password']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="confirm-password">{{ 'signIn.newPasswordAgain' | t }}</label>
                            <input id="confirm-password" type="password" autocomplete="new-password" [value]="confirm()" (input)="confirm.set(valueOf($event))">
                            @if (mismatch()) {
                                <span class="field-error">{{ 'common.passwordsDiffer' | t }}</span>
                            }
                        </div>
                        @if (error(); as error) {
                            <p class="form-error" role="alert">{{ error }}</p>
                        }
                        @if (changed()) {
                            <p class="form-ok" role="status">{{ 'account.passwordChanged' | t }}</p>
                        }
                        <div class="form-actions">
                            <button type="submit" class="button primary" [title]="'account.changePasswordHint' | t"
                                    [disabled]="busy() || !current() || !password() || password() !== confirm()">{{ 'account.changePassword' | t }}</button>
                        </div>
                    </form>
                </section>
            }

            @if (page() === 'preferences' && sessions.can(rights.ownPreferences)) {
                <section class="panel">
                    <lib-settings-panel level="user" (savedStates)="applied($event)" />
                </section>
            }
        }
    `,
})
export class AccountSection {
    readonly page = input.required<string>();

    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly theme = inject(ThemeService);
    protected readonly sessions = inject(SessionService);
    protected readonly rights = Rights;

    protected readonly current = signal('');
    protected readonly password = signal('');
    protected readonly confirm = signal('');
    protected readonly busy = signal(false);
    protected readonly changed = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly mismatch = computed(() => this.confirm() !== '' && this.confirm() !== this.password());

    protected readonly valueOf = valueOf;

    protected async changePassword(event: Event): Promise<void> {
        event.preventDefault();
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        this.changed.set(false);
        try {
            await this.api.changePassword(this.current(), this.password());
            this.current.set('');
            this.password.set('');
            this.confirm.set('');
            this.changed.set(true);
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

    /** The theme preference applies at once; the rest with the next pages. */
    protected applied(states: SettingState[]): void {
        const theme = states.find(state => state.key === 'ui.theme')?.value;
        this.theme.choose(theme === 'light' || theme === 'dark' ? theme : '');
    }
}
