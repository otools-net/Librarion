import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SettingState } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ThemeService } from '../core/theme.service';
import { SettingsPanel } from '../settings/settings-panel';

/** My account (as SFE's AccountSection): who is signed in, the password of a local account, own preferences, sign out. */
@Component({
    selector: 'lib-account-page',
    imports: [TranslatePipe, SettingsPanel],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <h1>{{ 'account.title' | t }}</h1>
        @if (sessions.user(); as user) {
            <p class="lead">{{ 'account.signedInAs' | t: { name: user.displayName || user.userName } }}
                @if (sessions.session()?.tenant; as tenant) {
                    · {{ tenant.name }}
                }
            </p>
            <div class="form-actions">
                <button type="button" class="button" [title]="'account.signOutHint' | t" (click)="signOut()">{{ 'account.signOut' | t }}</button>
            </div>

            @if (user.local && sessions.can(rights.ownPassword)) {
                <section class="panel">
                    <h2>{{ 'account.password' | t }}</h2>
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

            @if (sessions.can(rights.ownPreferences)) {
                <section class="panel">
                    <h2>{{ 'account.preferences' | t }}</h2>
                    <lib-settings-panel level="user" (savedStates)="applied($event)" />
                </section>
            }
        }
    `,
})
export class AccountPage {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly router = inject(Router);
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

    protected async signOut(): Promise<void> {
        await this.sessions.signOut();
        void this.router.navigate(['/login']);
    }
}
