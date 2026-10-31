import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { TenantInfo } from '../core/models';
import { SessionService } from '../core/session.service';

/**
 * Tenants (server administrators, as SFE's SpacesPage): created with their first library, renamed, disabled, deleted;
 * "Work here" switches the session to one.
 */
@Component({
    selector: 'lib-tenants-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <form class="toolbar" novalidate (submit)="create($event)">
            <input type="text" class="toolbar-search" [placeholder]="'tenants.newName' | t" [attr.aria-label]="'tenants.newName' | t"
                   [value]="name()" (input)="name.set(valueOf($event))">
            <button type="submit" class="button primary" [title]="'tenants.addHint' | t" [disabled]="busy() || !name().trim()">{{ 'tenants.add' | t }}</button>
        </form>
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.tenants' | t">
            @for (tenant of tenants(); track tenant.id) {
                <li class="row">
                    @if (renaming() === tenant.id) {
                        <form class="row-main row-form" novalidate (submit)="save($event, tenant)">
                            <input type="text" [attr.aria-label]="'tenants.name' | t" [value]="tenant.name" (input)="newName.set(valueOf($event))">
                            <label class="check">
                                <input type="checkbox" [checked]="newDisabled()" (change)="newDisabled.set(checkedOf($event))">
                                <span>{{ 'tenants.disabled' | t }}</span>
                            </label>
                            <button type="submit" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy()">{{ 'common.save' | t }}</button>
                            <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="renaming.set(null)">{{ 'common.cancel' | t }}</button>
                        </form>
                    } @else {
                        <span class="row-main">
                            <span class="row-title">{{ tenant.name }}</span>
                            <span class="row-meta">{{ tenant.slug }}</span>
                        </span>
                        @if (tenant.disabled) {
                            <span class="badge">{{ 'tenants.disabledBadge' | t }}</span>
                        }
                        @if (sessions.session()?.tenant?.id === tenant.id) {
                            <span class="badge">{{ 'tenants.current' | t }}</span>
                        } @else {
                            <button type="button" class="button" [title]="'tenants.workHereHint' | t: { name: tenant.name }" (click)="workHere(tenant)">{{ 'tenants.workHere' | t }}</button>
                        }
                        <button type="button" class="button" [title]="'tenants.editHint' | t: { name: tenant.name }" (click)="startEdit(tenant)">{{ 'common.edit' | t }}</button>
                        <button type="button" class="button danger" [title]="'tenants.deleteHint' | t: { name: tenant.name }" [disabled]="busy()" (click)="remove(tenant)">{{ 'common.delete' | t }}</button>
                    }
                </li>
            }
        </ul>
    `,
})
export class TenantsSection implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    protected readonly sessions = inject(SessionService);

    protected readonly tenants = signal<TenantInfo[]>([]);
    protected readonly name = signal('');
    protected readonly renaming = signal<number | null>(null);
    protected readonly newName = signal('');
    protected readonly newDisabled = signal(false);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    async ngOnInit(): Promise<void> {
        await this.run(async () => this.tenants.set(await this.api.tenants()));
    }

    protected async create(event: Event): Promise<void> {
        event.preventDefault();
        await this.run(async () => {
            await this.api.createTenant({ name: this.name().trim() });
            this.name.set('');
            await this.refresh();
        });
    }

    protected startEdit(tenant: TenantInfo): void {
        this.newName.set(tenant.name);
        this.newDisabled.set(tenant.disabled);
        this.renaming.set(tenant.id);
    }

    protected async save(event: Event, tenant: TenantInfo): Promise<void> {
        event.preventDefault();
        await this.run(async () => {
            await this.api.updateTenant(tenant.id, { name: this.newName().trim(), disabled: this.newDisabled() });
            this.renaming.set(null);
            await this.refresh();
        });
    }

    protected async workHere(tenant: TenantInfo): Promise<void> {
        await this.run(() => this.sessions.switchTenant(tenant.id));
    }

    protected async remove(tenant: TenantInfo): Promise<void> {
        if (prompt(this.i18n.t('tenants.deleteConfirm', { name: tenant.name }))?.trim() !== tenant.name)
            return;
        await this.run(async () => {
            await this.api.deleteTenant(tenant.id);
            await this.refresh();
        });
    }

    /** The list, and the session's tenants (the switcher in the top bar). */
    private async refresh(): Promise<void> {
        this.tenants.set(await this.api.tenants());
        this.sessions.set(await this.api.session());
    }

    private async run(work: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await work();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
