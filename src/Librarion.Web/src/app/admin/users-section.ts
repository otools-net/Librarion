import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { GroupInfo, UserInfo } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';

/** What the editor holds: a new account (`id` null) or one being changed. */
interface UserDraft {
    id: number | null;
    userName: string;
    displayName: string;
    password: string;
    disabled: boolean;
    isServerAdmin: boolean;
    groupIds: number[];
    lockedOut: boolean;
}

/**
 * Accounts of the tenant (as SFE's AccountsPage): searched by name, 100 at a time, and an editor for one account (name,
 * password, groups, disabled, unlock). With `server.users`, the server administrators can be listed and created too.
 */
@Component({
    selector: 'lib-users-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <input type="search" class="toolbar-search" [placeholder]="'users.search' | t" [attr.aria-label]="'users.search' | t"
                   [value]="search()" (input)="search.set(valueOf($event)); reload()">
            @if (sessions.can(rights.manageAllUsers)) {
                <label class="check">
                    <input type="checkbox" [checked]="admins()" (change)="admins.set(checkedOf($event)); reload()">
                    <span>{{ 'users.serverAdmins' | t }}</span>
                </label>
            }
            <button type="button" class="button primary" [title]="'users.newHint' | t" (click)="startNew()">{{ 'users.new' | t }}</button>
        </div>

        @if (draft(); as draft) {
            <form class="panel editor settings-form" novalidate (submit)="save($event)">
                <h2>{{ (draft.id === null ? 'users.newTitle' : 'users.editTitle') | t }}</h2>
                <div class="field">
                    <label class="field-label" for="user-name">{{ 'signIn.name' | t }}</label>
                    <input id="user-name" type="text" autocomplete="off" autocapitalize="none" spellcheck="false"
                           [value]="draft.userName" (input)="patch({ userName: valueOf($event) })">
                    @if (errors()['userName']; as error) {
                        <span class="field-error">{{ error }}</span>
                    }
                </div>
                <div class="field">
                    <label class="field-label" for="user-display">{{ 'users.displayName' | t }}</label>
                    <input id="user-display" type="text" autocomplete="off" [value]="draft.displayName" (input)="patch({ displayName: valueOf($event) })">
                </div>
                <div class="field">
                    <label class="field-label" for="user-password">{{ 'signIn.password' | t }}</label>
                    <input id="user-password" type="password" autocomplete="new-password" [value]="draft.password" (input)="patch({ password: valueOf($event) })">
                    @if (draft.id !== null) {
                        <span class="field-hint">{{ 'users.passwordKeep' | t }}</span>
                    }
                    @if (errors()['password']; as error) {
                        <span class="field-error">{{ error }}</span>
                    }
                </div>
                @if (!draft.isServerAdmin) {
                    <fieldset class="field">
                        <legend class="field-label">{{ 'users.groups' | t }}</legend>
                        <div class="checks">
                            @for (group of groups(); track group.id) {
                                <label class="check">
                                    <input type="checkbox" [checked]="draft.groupIds.includes(group.id)" (change)="toggleGroup(group.id, checkedOf($event))">
                                    <span>{{ groupName(group) }}</span>
                                </label>
                            }
                        </div>
                    </fieldset>
                }
                <label class="check">
                    <input type="checkbox" [checked]="draft.disabled" (change)="patch({ disabled: checkedOf($event) })">
                    <span>{{ 'users.disabled' | t }}<span class="field-hint">{{ 'users.disabledHint' | t }}</span></span>
                </label>
                @if (draft.lockedOut) {
                    <p class="field-hint">{{ 'users.lockedOut' | t }}</p>
                }
                @if (error(); as error) {
                    <p class="form-error" role="alert">{{ error }}</p>
                }
                <div class="form-actions">
                    <button type="submit" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy() || !draft.userName.trim()">{{ 'common.save' | t }}</button>
                    @if (draft.lockedOut) {
                        <button type="button" class="button" [title]="'users.unlockHint' | t" [disabled]="busy()" (click)="save($event, true)">{{ 'users.unlock' | t }}</button>
                    }
                    <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="draftState.set(null)">{{ 'common.cancel' | t }}</button>
                    @if (draft.id !== null) {
                        <button type="button" class="button danger" [title]="'users.deleteHint' | t" [disabled]="busy()" (click)="remove(draft)">{{ 'common.delete' | t }}</button>
                    }
                </div>
            </form>
        }

        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.users' | t">
            @for (user of users(); track user.id) {
                <li class="row">
                    <button type="button" class="row-main" [title]="'users.editHint' | t: { name: user.userName }" (click)="edit(user)">
                        <span class="row-title">{{ user.displayName || user.userName }}</span>
                        <span class="row-meta">{{ user.userName }} · {{ groupsOf(user) }}</span>
                    </button>
                    @if (user.disabled) {
                        <span class="badge">{{ 'users.disabledBadge' | t }}</span>
                    }
                    @if (user.lockedOut) {
                        <span class="badge danger">{{ 'users.lockedBadge' | t }}</span>
                    }
                </li>
            } @empty {
                <li class="row muted">{{ 'users.none' | t }}</li>
            }
        </ul>
        @if (more()) {
            <button type="button" class="button" [title]="'common.moreHint' | t" (click)="loadMore()">{{ 'common.more' | t }}</button>
        }
    `,
})
export class UsersSection implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    protected readonly sessions = inject(SessionService);
    protected readonly rights = Rights;

    protected readonly users = signal<UserInfo[]>([]);
    protected readonly groups = signal<GroupInfo[]>([]);
    protected readonly search = signal('');
    protected readonly admins = signal(false);
    protected readonly more = signal(false);
    protected readonly draftState = signal<UserDraft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly listError = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    private static readonly PAGE = 100;

    async ngOnInit(): Promise<void> {
        if (this.sessions.canAny(Rights.manageUsers, Rights.manageTenants))
            this.api.groups().then(groups => this.groups.set(groups), () => { });
        await this.reload();
    }

    protected async reload(): Promise<void> {
        await this.load([]);
    }

    protected async loadMore(): Promise<void> {
        await this.load(this.users());
    }

    protected groupName(group: GroupInfo): string {
        return group.builtIn ? this.i18n.t(`group.${group.builtIn}`) : group.name;
    }

    protected groupsOf(user: UserInfo): string {
        if (user.isServerAdmin)
            return this.i18n.t('users.serverAdmin');
        const names = user.groups.map(membership => {
            const group = this.groups().find(g => g.id === membership.groupId);
            return group ? this.groupName(group) : membership.group;
        });
        return names.length ? names.join(', ') : this.i18n.t('users.noGroup');
    }

    protected startNew(): void {
        this.open({ id: null, userName: '', displayName: '', password: '', disabled: false, isServerAdmin: this.admins(), groupIds: [], lockedOut: false });
    }

    protected edit(user: UserInfo): void {
        this.open({
            id: user.id, userName: user.userName, displayName: user.displayName ?? '', password: '', disabled: user.disabled,
            isServerAdmin: user.isServerAdmin, groupIds: user.groups.map(g => g.groupId), lockedOut: user.lockedOut,
        });
    }

    protected patch(change: Partial<UserDraft>): void {
        this.draftState.update(draft => draft && { ...draft, ...change });
    }

    protected toggleGroup(id: number, on: boolean): void {
        this.draftState.update(draft => draft && { ...draft, groupIds: on ? [...draft.groupIds, id] : draft.groupIds.filter(g => g !== id) });
    }

    protected async save(event: Event, unlock = false): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft)
            return;
        await this.run(async () => {
            const input = {
                userName: draft.userName.trim(),
                displayName: draft.displayName.trim() || null,
                password: draft.password || null,
                disabled: draft.disabled,
                isServerAdmin: draft.isServerAdmin,
                groupIds: draft.isServerAdmin ? null : draft.groupIds,
                unlock,
            };
            if (draft.id === null)
                await this.api.createUser(input);
            else
                await this.api.updateUser(draft.id, input);
            this.draftState.set(null);
            await this.reload();
        });
    }

    protected async remove(draft: UserDraft): Promise<void> {
        if (draft.id === null || !confirm(this.i18n.t('users.deleteConfirm', { name: draft.userName })))
            return;
        await this.run(async () => {
            await this.api.deleteUser(draft.id!);
            this.draftState.set(null);
            await this.reload();
        });
    }

    private open(draft: UserDraft): void {
        this.error.set(null);
        this.errors.set({});
        this.draftState.set(draft);
    }

    private async run(work: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            await work();
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

    private async load(before: UserInfo[]): Promise<void> {
        this.listError.set(null);
        try {
            const page = await this.api.users({ admins: this.admins(), search: this.search().trim(), after: before.at(-1)?.userName });
            this.users.set([...before, ...page]);
            this.more.set(page.length === UsersSection.PAGE);
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        }
    }
}
