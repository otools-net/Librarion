import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { RightInfo, TenantInfo } from '../core/models';
import { rightAreas } from '../core/rights';
import { SessionService } from '../core/session.service';

/** What the editor holds for the tenant being changed. */
interface TenantDraft {
    id: number;
    name: string;
    disabled: boolean;
    disabledRights: string[];
}

/**
 * Tenants (server administrators, as SFE's SpacesPage): created with their first library, renamed, disabled, rights
 * switched off for everyone in it, deleted; "Work here" switches the session to one.
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
        @if (draft(); as draft) {
            <form class="panel editor settings-form" novalidate (submit)="save($event)">
                <h2>{{ 'tenants.editTitle' | t }}</h2>
                <div class="field">
                    <label class="field-label" for="tenant-name">{{ 'tenants.name' | t }}</label>
                    <input id="tenant-name" type="text" autocomplete="off" [value]="draft.name" (input)="patch({ name: valueOf($event) })">
                </div>
                <label class="check">
                    <input type="checkbox" [checked]="draft.disabled" (change)="patch({ disabled: checkedOf($event) })">
                    <span>{{ 'tenants.disabled' | t }}<span class="field-hint">{{ 'tenants.disabledHint' | t }}</span></span>
                </label>
                <div class="field">
                    <h3 class="field-label">{{ 'tenants.disabledRights' | t }}</h3>
                    <span class="field-hint">{{ 'tenants.disabledRightsHint' | t }}</span>
                </div>
                @for (area of areas(); track area.name) {
                    <fieldset class="field">
                        <legend class="field-label">{{ 'rightArea.' + area.name | t }}</legend>
                        <div class="checks">
                            @for (right of area.rights; track right.id) {
                                <label class="check" [title]="'right.' + right.id + '.hint' | t">
                                    <input type="checkbox" [checked]="draft.disabledRights.includes(right.id) || right.disabledByServer" [disabled]="right.disabledByServer"
                                           (change)="toggleRight(right.id, checkedOf($event))">
                                    <span>{{ 'right.' + right.id | t }}
                                        @if (right.disabledByServer) {
                                            <span class="badge danger">{{ 'groups.disabledByServer' | t }}</span>
                                        }
                                    </span>
                                </label>
                            }
                        </div>
                    </fieldset>
                }
                <div class="form-actions">
                    <button type="submit" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy() || !draft.name.trim()">{{ 'common.save' | t }}</button>
                    <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="draftState.set(null)">{{ 'common.cancel' | t }}</button>
                </div>
            </form>
        }
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.tenants' | t">
            @for (tenant of tenants(); track tenant.id) {
                <li class="row">
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
    protected readonly catalog = signal<RightInfo[]>([]);
    protected readonly areas = computed(() => rightAreas(this.catalog()));
    protected readonly name = signal('');
    protected readonly draftState = signal<TenantDraft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    async ngOnInit(): Promise<void> {
        await this.run(async () => {
            const [tenants, catalog] = await Promise.all([this.api.tenants(), this.api.rights()]);
            this.tenants.set(tenants);
            this.catalog.set(catalog);
        });
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
        this.draftState.set({ id: tenant.id, name: tenant.name, disabled: tenant.disabled, disabledRights: [...tenant.disabledRights] });
    }

    protected patch(change: Partial<TenantDraft>): void {
        this.draftState.update(draft => draft && { ...draft, ...change });
    }

    protected toggleRight(id: string, off: boolean): void {
        this.draftState.update(draft => draft && {
            ...draft,
            disabledRights: off ? [...draft.disabledRights.filter(r => r !== id), id] : draft.disabledRights.filter(r => r !== id),
        });
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft)
            return;
        await this.run(async () => {
            await this.api.updateTenant(draft.id, { name: draft.name.trim(), disabled: draft.disabled, disabledRights: draft.disabledRights });
            this.draftState.set(null);
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
