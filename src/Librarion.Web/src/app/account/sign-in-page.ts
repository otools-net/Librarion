import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Api, ApiError } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SessionService } from '../core/session.service';

/**
 * The sign-in page (`login?return=`), ported from SFE: a name and password; back to `return` once signed in. A
 * server administrator locked out (or without the password) recovers the account here: the server shows a one-time
 * code where it runs, and the code with a new password unlocks the account and signs it in.
 */
@Component({
    selector: 'lib-sign-in-page',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'account-page' },
    template: `
        <main class="account-card">
            <header class="account-head">
                <span class="brand"><img src="favicon.svg" alt="" width="24" height="24"><span>{{ 'app.name' | t }}</span></span>
                <h1>{{ (recovering() ? 'signIn.recoverTitle' : 'signIn.title') | t }}</h1>
            </header>
            @if (!ready()) {
                <p class="muted" aria-busy="true">{{ 'common.loading' | t }}</p>
            } @else if (recovering()) {
                <form class="account-form" novalidate (submit)="recover($event)">
                    <p class="field-hint">{{ 'signIn.recoverIntro' | t }}</p>
                    <div class="field">
                        <label class="field-label" for="recover-name">{{ 'signIn.name' | t }}</label>
                        <input id="recover-name" type="text" autocomplete="username" autocapitalize="none" spellcheck="false" required
                               [value]="userName()" (input)="userName.set(valueOf($event))">
                    </div>
                    @if (codeMinutes() === null) {
                        @if (error(); as error) {
                            <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <button type="button" class="button primary account-submit" [title]="'signIn.requestCodeHint' | t"
                                [disabled]="busy() || !userName().trim()" (click)="requestCode()">{{ 'signIn.requestCode' | t }}</button>
                    } @else {
                        <p class="field-hint" role="status">{{ 'signIn.codeShown' | t: { minutes: codeMinutes()! } }}</p>
                        <div class="field">
                            <label class="field-label" for="recover-code">{{ 'signIn.code' | t }}</label>
                            <input id="recover-code" type="text" autocomplete="one-time-code" autocapitalize="characters" spellcheck="false" required
                                   [value]="code()" (input)="code.set(valueOf($event))">
                            @if (errors()['code']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="recover-password">{{ 'signIn.newPassword' | t }}</label>
                            <input id="recover-password" type="password" autocomplete="new-password" required
                                   [value]="password()" (input)="password.set(valueOf($event))">
                            @if (errors()['password']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="recover-confirm">{{ 'signIn.newPasswordAgain' | t }}</label>
                            <input id="recover-confirm" type="password" autocomplete="new-password" required
                                   [value]="confirm()" (input)="confirm.set(valueOf($event))">
                            @if (mismatch()) {
                                <span class="field-error">{{ 'common.passwordsDiffer' | t }}</span>
                            }
                        </div>
                        @if (error(); as error) {
                            <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <button type="submit" class="button primary account-submit" [title]="'signIn.recoverSubmitHint' | t"
                                [disabled]="busy() || !userName().trim() || !code().trim() || !password() || password() !== confirm()">{{ 'signIn.recoverSubmit' | t }}</button>
                    }
                    <button type="button" class="button account-submit" [title]="'signIn.backHint' | t" (click)="leaveRecovery()">{{ 'common.back' | t }}</button>
                </form>
            } @else {
                <form class="account-form" novalidate (submit)="signIn($event)">
                    <div class="field">
                        <label class="field-label" for="login-name">{{ 'signIn.name' | t }}</label>
                        <input id="login-name" type="text" autocomplete="username" autocapitalize="none" spellcheck="false" required
                               [value]="userName()" (input)="userName.set(valueOf($event))">
                    </div>
                    <div class="field">
                        <label class="field-label" for="login-password">{{ 'signIn.password' | t }}</label>
                        <input id="login-password" type="password" autocomplete="current-password" required
                               [value]="password()" (input)="password.set(valueOf($event))">
                    </div>
                    <label class="check">
                        <input type="checkbox" [checked]="remember()" (change)="remember.set(checkedOf($event))">
                        <span>{{ 'signIn.remember' | t }}<span class="field-hint">{{ 'signIn.rememberHint' | t }}</span></span>
                    </label>
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                    <button type="submit" class="button primary account-submit" [title]="'signIn.submitHint' | t"
                            [disabled]="busy() || !userName().trim() || !password()">{{ 'signIn.submit' | t }}</button>
                </form>
                <p class="field-hint account-foot">
                    {{ 'signIn.lockedOut' | t }}
                    <a href="" [title]="'signIn.recoverLinkHint' | t" (click)="startRecovery($event)">{{ 'signIn.recoverLink' | t }}</a>
                </p>
            }
        </main>
    `,
})
export class SignInPage implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly sessions = inject(SessionService);
    private readonly router = inject(Router);
    private readonly returnUrl = inject(ActivatedRoute).snapshot.queryParamMap.get('return');

    protected readonly ready = signal(false);
    protected readonly userName = signal('');
    protected readonly password = signal('');
    protected readonly remember = signal(false);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    /** Recovering an administrator's account: with a code shown where the server runs, a new password. */
    protected readonly recovering = signal(false);
    /** How long the code works, once asked for; null before. */
    protected readonly codeMinutes = signal<number | null>(null);
    protected readonly code = signal('');
    protected readonly confirm = signal('');
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly mismatch = computed(() => this.confirm() !== '' && this.confirm() !== this.password());

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    async ngOnInit(): Promise<void> {
        try {
            const session = await this.api.session();
            this.sessions.set(session);
            if (session.setupRequired) {
                void this.router.navigate(['/setup'], { replaceUrl: true });
                return;
            }
            if (session.user) {
                this.done();
                return;
            }
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
        this.ready.set(true);
    }

    protected async signIn(event: Event): Promise<void> {
        event.preventDefault();
        if (!this.userName().trim() || !this.password())
            return;
        this.busy.set(true);
        this.error.set(null);
        try {
            this.sessions.set(await this.api.signIn(this.userName().trim(), this.password(), this.remember()));
            this.done();
        } catch (error) {
            this.error.set(error instanceof ApiError && error.type === 'locked-out' && typeof error.extensions['until'] === 'string'
                ? this.i18n.t('error.locked-out-until', { time: new Date(error.extensions['until']).toLocaleTimeString(this.i18n.language()) })
                : errorText(this.i18n, error));
            this.password.set('');
        } finally {
            this.busy.set(false);
        }
    }

    protected startRecovery(event: Event): void {
        event.preventDefault();
        this.recovering.set(true);
        this.password.set('');
        this.error.set(null);
    }

    protected leaveRecovery(): void {
        this.recovering.set(false);
        this.codeMinutes.set(null);
        this.code.set('');
        this.password.set('');
        this.confirm.set('');
        this.errors.set({});
        this.error.set(null);
    }

    protected async requestCode(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            this.codeMinutes.set((await this.api.requestRecovery(this.userName().trim())).minutes);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async recover(event: Event): Promise<void> {
        event.preventDefault();
        if (!this.code().trim() || !this.password() || this.password() !== this.confirm())
            return;
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            this.sessions.set(await this.api.completeRecovery(this.userName().trim(), this.code().trim(), this.password(), this.remember()));
            this.done();
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

    /** Signed in: where the page was asked from. */
    private done(): void {
        void this.router.navigateByUrl(this.returnUrl && this.returnUrl.startsWith('/') && !this.returnUrl.startsWith('//') ? this.returnUrl : '/', { replaceUrl: true });
    }
}
