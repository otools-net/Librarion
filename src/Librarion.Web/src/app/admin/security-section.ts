import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SecurityEventInfo } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';

/** Event kinds, as the server names them (texts: `securityEvent.<kind>`). */
export const SECURITY_EVENT_KINDS = [
    'signedIn', 'signInFailed', 'lockedOut', 'signedOut', 'passwordChanged', 'setupCompleted', 'recoveryRequested', 'recoveryCompleted',
    'userCreated', 'userChanged', 'userDeleted', 'groupCreated', 'groupChanged', 'groupDeleted', 'tenantCreated', 'tenantChanged',
    'tenantDeleted', 'libraryCreated', 'libraryChanged', 'libraryDeleted', 'settingsChanged',
];

/** The security log, newest first, 100 at a time; filtered by kind and name. Server administrators may see every tenant's. */
@Component({
    selector: 'lib-security-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <select [attr.aria-label]="'security.kind' | t" (change)="kind.set(valueOf($event)); reload()">
                <option value="">{{ 'security.allKinds' | t }}</option>
                @for (option of kinds; track option) {
                    <option [value]="option">{{ 'securityEvent.' + option | t }}</option>
                }
            </select>
            <input type="search" class="toolbar-search" [placeholder]="'security.user' | t" [attr.aria-label]="'security.user' | t"
                   [value]="user()" (change)="user.set(valueOf($event)); reload()">
            @if (sessions.can(rights.serverSecurityLog)) {
                <label class="check">
                    <input type="checkbox" [checked]="all()" (change)="all.set(checkedOf($event)); reload()">
                    <span>{{ 'security.allTenants' | t }}</span>
                </label>
            }
        </div>
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.security' | t">
            @for (event of events(); track event.id) {
                <li class="row event">
                    <span class="row-main">
                        <span class="row-title">{{ 'securityEvent.' + event.kind | t }}</span>
                        <span class="row-meta">{{ when(event.time) }} · {{ event.userName ?? '—' }}@if (event.address) { · {{ event.address }} }</span>
                        @if (event.detail) {
                            <span class="row-meta">{{ event.detail }}</span>
                        }
                    </span>
                </li>
            } @empty {
                <li class="row muted">{{ 'security.none' | t }}</li>
            }
        </ul>
        @if (more()) {
            <button type="button" class="button" [title]="'common.moreHint' | t" (click)="load(events())">{{ 'common.more' | t }}</button>
        }
    `,
})
export class SecuritySection implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    protected readonly sessions = inject(SessionService);
    protected readonly rights = Rights;
    protected readonly kinds = SECURITY_EVENT_KINDS;

    protected readonly events = signal<SecurityEventInfo[]>([]);
    protected readonly kind = signal('');
    protected readonly user = signal('');
    protected readonly all = signal(false);
    protected readonly more = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    async ngOnInit(): Promise<void> {
        await this.reload();
    }

    protected async reload(): Promise<void> {
        await this.load([]);
    }

    protected async load(before: SecurityEventInfo[]): Promise<void> {
        this.error.set(null);
        try {
            const page = await this.api.securityEvents({ before: before.at(-1)?.id, kind: this.kind(), user: this.user().trim(), all: this.all() });
            this.events.set([...before, ...page]);
            this.more.set(page.length === 100);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected when(time: string): string {
        return new Date(time).toLocaleString(this.i18n.language());
    }
}
