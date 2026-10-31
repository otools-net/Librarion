import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SetupState } from '../core/models';
import { SessionService } from '../core/session.service';

/**
 * The first start (as SFE's setup page): the setup code the server printed (and wrote to its data folder), the first
 * server administrator, and the names of the first tenant and its library. With `Librarion:Setup:Recover`, the same
 * page resets a local server administrator.
 */
@Component({
    selector: 'lib-setup-page',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'account-page' },
    template: `
        <main class="account-card wide">
            <header class="account-head">
                <span class="brand"><img src="favicon.svg" alt="" width="24" height="24"><span>{{ 'app.name' | t }}</span></span>
                <h1>{{ (state()?.recover ? 'setup.recoverTitle' : 'setup.title') | t }}</h1>
            </header>
            @if (!state()) {
                @if (error(); as error) {
                    <p class="form-error" role="alert">{{ error }}</p>
                } @else {
                    <p class="muted" aria-busy="true">{{ 'common.loading' | t }}</p>
                }
            } @else if (!state()!.available) {
                <p>{{ 'setup.done' | t }}</p>
                <button type="button" class="button primary account-submit" [title]="'setup.toSignInHint' | t" (click)="toSignIn()">{{ 'signIn.submit' | t }}</button>
            } @else {
                <form class="account-form" novalidate (submit)="complete($event)">
                    <p>{{ (state()!.recover ? 'setup.recoverIntro' : 'setup.intro') | t }}</p>
                    <div class="field">
                        <label class="field-label" for="setup-code">{{ 'setup.code' | t }}</label>
                        <input id="setup-code" type="text" autocomplete="off" autocapitalize="characters" spellcheck="false" required
                               [value]="code()" (input)="code.set(valueOf($event))">
                        <span class="field-hint">
                            @if (state()!.codeFile; as file) {
                                {{ 'setup.codeGenerated' | t: { file } }}
                            } @else {
                                {{ 'setup.codeConfigured' | t }}
                            }
                        </span>
                        @if (errors()['code']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </div>
                    <fieldset class="account-fieldset">
                        <legend class="field-label">{{ 'setup.administrator' | t }}</legend>
                        <div class="field">
                            <label class="field-label" for="setup-name">{{ 'signIn.name' | t }}</label>
                            <input id="setup-name" type="text" autocomplete="username" autocapitalize="none" spellcheck="false" required
                                   [value]="userName()" (input)="userName.set(valueOf($event))">
                            @if (errors()['userName']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="setup-password">{{ 'signIn.password' | t }}</label>
                            <input id="setup-password" type="password" autocomplete="new-password" required
                                   [value]="password()" (input)="password.set(valueOf($event))">
                            @if (errors()['password']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="setup-confirm">{{ 'signIn.newPasswordAgain' | t }}</label>
                            <input id="setup-confirm" type="password" autocomplete="new-password" required
                                   [value]="confirm()" (input)="confirm.set(valueOf($event))">
                            @if (mismatch()) {
                                <span class="field-error">{{ 'common.passwordsDiffer' | t }}</span>
                            }
                        </div>
                    </fieldset>
                    @if (!state()!.recover) {
                        <div class="field">
                            <label class="field-label" for="setup-tenant">{{ 'setup.tenantName' | t }}</label>
                            <input id="setup-tenant" type="text" [placeholder]="'setup.tenantPlaceholder' | t"
                                   [value]="tenantName()" (input)="tenantName.set(valueOf($event))">
                            <span class="field-hint">{{ 'setup.tenantHint' | t }}</span>
                        </div>
                        <div class="field">
                            <label class="field-label" for="setup-library">{{ 'setup.libraryName' | t }}</label>
                            <input id="setup-library" type="text" [placeholder]="'setup.libraryPlaceholder' | t"
                                   [value]="libraryName()" (input)="libraryName.set(valueOf($event))">
                        </div>
                    }
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                    <button type="submit" class="button primary account-submit" [title]="'setup.submitHint' | t"
                            [disabled]="busy() || !code().trim() || !userName().trim() || !password() || mismatch()">{{ 'setup.submit' | t }}</button>
                </form>
            }
        </main>
    `,
})
export class SetupPage implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly sessions = inject(SessionService);
    private readonly router = inject(Router);

    protected readonly state = signal<SetupState | null>(null);
    protected readonly code = signal('');
    protected readonly userName = signal('');
    protected readonly password = signal('');
    protected readonly confirm = signal('');
    protected readonly tenantName = signal('');
    protected readonly libraryName = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly mismatch = computed(() => this.confirm() !== '' && this.confirm() !== this.password());

    protected readonly valueOf = valueOf;

    async ngOnInit(): Promise<void> {
        try {
            this.state.set(await this.api.setup());
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected async complete(event: Event): Promise<void> {
        event.preventDefault();
        if (this.busy() || this.mismatch())
            return;
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            this.sessions.set(await this.api.completeSetup({
                code: this.code().trim(),
                userName: this.userName().trim(),
                password: this.password(),
                tenantName: this.tenantName().trim() || null,
                libraryName: this.libraryName().trim() || null,
            }));
            void this.router.navigateByUrl('/', { replaceUrl: true });
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

    protected toSignIn(): void {
        void this.router.navigate(['/login'], { replaceUrl: true });
    }
}
