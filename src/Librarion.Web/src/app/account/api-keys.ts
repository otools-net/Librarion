import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { API_KEY_SCOPES, ApiKeyInfo, ApiKeyScope, NewApiKey } from '../core/models';
import { SessionService } from '../core/session.service';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';

/** The text of a scope, as the form and the list name it (`apiKeys.scope.<scope>`). */
export const scopeKey = (scope: ApiKeyScope) => `apiKeys.scope.${scope.replace(':', '-')}`;

/**
 * API keys (agents/docs/features/opds-auth.md): one's own on My account › API keys (made, shown once, revoked), or an
 * account's for its administrators (`userId`: listed and revoked only). Managing keys needs the web session; a key can't.
 */
@Component({
    selector: 'lib-api-keys',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (own()) {
            <section class="panel">
                <p class="field-hint">{{ 'apiKeys.intro' | t }}</p>
                <p class="field-hint">{{ 'apiKeys.opdsAddress' | t: { address: opdsAddress, name: sessions.user()?.userName ?? '' } }}</p>
            </section>

            @if (made(); as made) {
                <section class="panel api-key-made" role="status">
                    <h2>{{ 'apiKeys.madeTitle' | t: { name: made.info.name } }}</h2>
                    <p class="notice accent">{{ 'apiKeys.madeOnce' | t }}</p>
                    <div class="field">
                        <label class="field-label" for="api-key-text">{{ 'apiKeys.key' | t }}</label>
                        <input id="api-key-text" type="text" class="api-key-text" readonly spellcheck="false" [value]="made.key" (focus)="select($event)">
                    </div>
                    <div class="form-actions">
                        <button type="button" class="button primary" [title]="'apiKeys.copyHint' | t" (click)="copy(made.key)">{{ 'apiKeys.copy' | t }}</button>
                        <button type="button" class="button" [title]="'apiKeys.doneHint' | t" (click)="madeState.set(null)">{{ 'apiKeys.done' | t }}</button>
                    </div>
                </section>
            } @else if (creating()) {
                <form class="panel editor settings-form" novalidate (submit)="create($event)">
                    <h2>{{ 'apiKeys.newTitle' | t }}</h2>
                    <div class="field">
                        <label class="field-label" for="api-key-name">{{ 'apiKeys.name' | t }}</label>
                        <input id="api-key-name" type="text" autocomplete="off" maxlength="100" [placeholder]="'apiKeys.namePlaceholder' | t"
                               [value]="name()" (input)="name.set(valueOf($event))">
                        @if (errors()['name']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </div>
                    <fieldset class="field">
                        <legend class="field-label">{{ 'apiKeys.scopes' | t }}</legend>
                        <div class="checks">
                            @for (scope of allScopes; track scope) {
                                <label class="check">
                                    <input type="checkbox" [checked]="scopes().includes(scope)" (change)="toggleScope(scope, checkedOf($event))">
                                    <span>{{ scopeKey(scope) | t }}<span class="field-hint">{{ scopeKey(scope) + '.hint' | t }}</span></span>
                                </label>
                            }
                        </div>
                        @if (errors()['scopes']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </fieldset>
                    @if (libraries().length > 1) {
                        <fieldset class="field">
                            <legend class="field-label">{{ 'apiKeys.libraries' | t }}</legend>
                            <span class="field-hint">{{ 'apiKeys.librariesHint' | t }}</span>
                            <div class="checks">
                                @for (library of libraries(); track library.id) {
                                    <label class="check">
                                        <input type="checkbox" [checked]="libraryIds().includes(library.id)" (change)="toggleLibrary(library.id, checkedOf($event))">
                                        <span>{{ library.name }}</span>
                                    </label>
                                }
                            </div>
                            @if (errors()['libraryIds']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </fieldset>
                    }
                    <div class="field">
                        <label class="field-label" for="api-key-expires">{{ 'apiKeys.expires' | t }}</label>
                        <input id="api-key-expires" type="date" [min]="tomorrow" [value]="expires()" (input)="expires.set(valueOf($event))">
                        <span class="field-hint">{{ 'apiKeys.expiresHint' | t }}</span>
                        @if (errors()['expires']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </div>
                    @if (scopes().includes('opds')) {
                        <label class="check">
                            <input type="checkbox" [checked]="pathAllowed()" (change)="pathAllowed.set(checkedOf($event))">
                            <span>{{ 'apiKeys.pathAllowed' | t }}<span class="field-hint">{{ 'apiKeys.pathAllowedHint' | t }}</span></span>
                        </label>
                    }
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                    <div class="form-actions">
                        <button type="submit" class="button primary" [title]="'apiKeys.createHint' | t"
                                [disabled]="busy() || !name().trim() || scopes().length === 0">{{ 'apiKeys.create' | t }}</button>
                        <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="creating.set(false)">{{ 'common.cancel' | t }}</button>
                    </div>
                </form>
            } @else {
                <div class="toolbar">
                    <button type="button" class="button primary" [title]="'apiKeys.newHint' | t" (click)="startNew()">{{ 'apiKeys.new' | t }}</button>
                </div>
            }
        }

        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'apiKeys.list' | t" [attr.aria-busy]="loading()">
            @for (key of keys(); track key.id) {
                <li class="row api-key-row" [class.api-key-ended]="ended(key)">
                    <span class="row-main">
                        <span class="row-title">{{ key.name }}</span>
                        <span class="row-meta">{{ scopesText(key) }} · {{ librariesText(key) }}@if (key.pathAllowed) { · {{ 'apiKeys.inAddress' | t }} }</span>
                        <span class="row-meta">{{ usedText(key) }}</span>
                    </span>
                    @if (key.revoked) {
                        <span class="badge">{{ 'apiKeys.revoked' | t }}</span>
                    } @else if (expired(key)) {
                        <span class="badge">{{ 'apiKeys.expired' | t }}</span>
                    } @else {
                        <button type="button" class="button danger" [title]="'apiKeys.revokeHint' | t: { name: key.name }" [disabled]="busy()"
                                (click)="revoke(key)">{{ 'apiKeys.revoke' | t }}</button>
                    }
                </li>
            } @empty {
                @if (!loading()) {
                    <li class="row">{{ 'apiKeys.none' | t }}</li>
                }
            }
        </ul>
    `,
})
export class ApiKeys implements OnInit {
    /** An account's keys, for its administrators (list and revoke); none = the signed-in account's own. */
    readonly userId = input<number | null>(null);

    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly context = inject(LibraryContext);
    protected readonly sessions = inject(SessionService);

    protected readonly own = computed(() => this.userId() === null);
    protected readonly libraries = this.context.libraries;
    protected readonly keys = signal<ApiKeyInfo[]>([]);
    protected readonly loading = signal(true);
    protected readonly creating = signal(false);
    protected readonly madeState = signal<NewApiKey | null>(null);
    protected readonly made = this.madeState.asReadonly();
    protected readonly name = signal('');
    protected readonly scopes = signal<ApiKeyScope[]>(['opds']);
    protected readonly libraryIds = signal<number[]>([]);
    protected readonly pathAllowed = signal(false);
    protected readonly expires = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly listError = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});

    protected readonly allScopes = API_KEY_SCOPES;
    protected readonly scopeKey = scopeKey;
    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    /** Where reader apps find the catalog: the app's root + `opds`, behind a proxy's virtual folder too. */
    protected readonly opdsAddress = new URL('opds', document.baseURI).href;
    protected readonly tomorrow = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10);

    async ngOnInit(): Promise<void> {
        await this.reload();
    }

    protected startNew(): void {
        this.name.set('');
        this.scopes.set(['opds']);
        this.libraryIds.set([]);
        this.pathAllowed.set(false);
        this.expires.set('');
        this.error.set(null);
        this.errors.set({});
        this.creating.set(true);
    }

    protected toggleScope(scope: ApiKeyScope, on: boolean): void {
        this.scopes.update(scopes => on ? API_KEY_SCOPES.filter(s => s === scope || scopes.includes(s)) : scopes.filter(s => s !== scope));
    }

    protected toggleLibrary(id: number, on: boolean): void {
        this.libraryIds.update(ids => on ? [...ids, id] : ids.filter(i => i !== id));
    }

    protected async create(event: Event): Promise<void> {
        event.preventDefault();
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            const made = await this.api.createApiKey({
                name: this.name().trim(),
                scopes: this.scopes(),
                libraryIds: this.libraryIds(),
                pathAllowed: this.scopes().includes('opds') && this.pathAllowed(),
                // The end of the day chosen, in the browser's time zone.
                expires: this.expires() ? new Date(`${this.expires()}T23:59:59`).toISOString() : null,
            });
            this.creating.set(false);
            this.madeState.set(made);
            await this.reload();
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

    protected async copy(text: string): Promise<void> {
        try {
            await navigator.clipboard.writeText(text);
            this.toasts.show(this.i18n.t('apiKeys.copied'));
        } catch {
            // No clipboard (plain http on another computer): the text is selected for the person to copy.
            document.querySelector<HTMLInputElement>('#api-key-text')?.select();
            this.toasts.show(this.i18n.t('apiKeys.copyByHand'));
        }
    }

    protected select(event: Event): void {
        (event.target as HTMLInputElement).select();
    }

    protected async revoke(key: ApiKeyInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('apiKeys.revokeTitle'), message: this.i18n.t('apiKeys.revokeConfirm', { name: key.name }),
            action: this.i18n.t('apiKeys.revoke'), danger: true,
        });
        if (!ok)
            return;
        this.busy.set(true);
        this.listError.set(null);
        try {
            const userId = this.userId();
            if (userId === null)
                await this.api.revokeApiKey(key.id);
            else
                await this.api.revokeUserApiKey(userId, key.id);
            this.toasts.show(this.i18n.t('apiKeys.revokedToast', { name: key.name }));
            if (this.made()?.info.id === key.id)
                this.madeState.set(null);
            await this.reload();
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected expired(key: ApiKeyInfo): boolean {
        return key.expires !== null && new Date(key.expires).getTime() <= Date.now();
    }

    protected ended(key: ApiKeyInfo): boolean {
        return key.revoked !== null || this.expired(key);
    }

    protected scopesText(key: ApiKeyInfo): string {
        return key.scopes.map(scope => this.i18n.t(scopeKey(scope))).join(', ');
    }

    protected librariesText(key: ApiKeyInfo): string {
        if (key.libraryIds.length === 0)
            return this.i18n.t('apiKeys.allLibraries');
        const names = key.libraryIds.map(id => this.libraries().find(library => library.id === id)?.name ?? `#${id}`);
        return names.join(', ');
    }

    protected usedText(key: ApiKeyInfo): string {
        const parts = [this.i18n.t('apiKeys.createdOn', { time: this.when(key.created) })];
        parts.push(key.lastUsed
            ? this.i18n.t('apiKeys.lastUsed', { time: this.when(key.lastUsed), address: key.lastAddress ?? '—' })
            : this.i18n.t('apiKeys.neverUsed'));
        if (key.revoked)
            parts.push(this.i18n.t('apiKeys.revokedOn', { time: this.when(key.revoked) }));
        else if (key.expires)
            parts.push(this.i18n.t(this.expired(key) ? 'apiKeys.expiredOn' : 'apiKeys.expiresOn', { time: this.when(key.expires) }));
        return parts.join(' · ');
    }

    private when(utc: string): string {
        return new Date(utc).toLocaleString(this.i18n.language());
    }

    private async reload(): Promise<void> {
        this.listError.set(null);
        try {
            const userId = this.userId();
            this.keys.set(userId === null ? await this.api.apiKeys() : await this.api.userApiKeys(userId));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.loading.set(false);
        }
    }
}
