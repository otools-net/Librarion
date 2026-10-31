import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { GroupInfo, LibraryInfo, RightInfo, UserInfo } from '../core/models';

interface GroupDraft {
    id: number | null;
    builtIn: string | null;
    name: string;
    allLibraries: boolean;
    rights: string[];
    libraryIds: number[];
    memberIds: number[];
}

/**
 * Groups of the tenant (as SFE's GroupEditor): their rights by area (each marked when it changes things), the
 * libraries their library rights apply in, and their members. Built-in groups keep their names and can't be deleted.
 */
@Component({
    selector: 'lib-groups-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <button type="button" class="button primary" [title]="'groups.newHint' | t" (click)="startNew()">{{ 'groups.new' | t }}</button>
        </div>

        @if (draft(); as draft) {
            <form class="panel editor settings-form" novalidate (submit)="save($event)">
                <h2>{{ (draft.id === null ? 'groups.newTitle' : 'groups.editTitle') | t }}</h2>
                <div class="field">
                    <label class="field-label" for="group-name">{{ 'groups.name' | t }}</label>
                    <input id="group-name" type="text" autocomplete="off" [disabled]="draft.builtIn !== null"
                           [value]="draft.builtIn ? ('group.' + draft.builtIn | t) : draft.name" (input)="patch({ name: valueOf($event) })">
                    @if (errors()['name']; as error) {
                        <span class="field-error">{{ error }}</span>
                    }
                </div>
                @for (area of areas(); track area.name) {
                    <fieldset class="field">
                        <legend class="field-label">{{ 'rightArea.' + area.name | t }}</legend>
                        <div class="checks">
                            @for (right of area.rights; track right.id) {
                                <label class="check" [title]="'right.' + right.id + '.hint' | t">
                                    <input type="checkbox" [checked]="draft.rights.includes(right.id)" [disabled]="right.disabledByServer"
                                           (change)="toggle('rights', right.id, checkedOf($event))">
                                    <span>{{ 'right.' + right.id | t }}
                                        @if (right.kind === 'write') {
                                            <span class="badge">{{ 'groups.changes' | t }}</span>
                                        }
                                        @if (right.disabledByServer) {
                                            <span class="badge danger">{{ 'groups.disabledByServer' | t }}</span>
                                        }
                                    </span>
                                </label>
                            }
                        </div>
                    </fieldset>
                }
                <fieldset class="field">
                    <legend class="field-label">{{ 'groups.libraries' | t }}</legend>
                    <label class="check">
                        <input type="checkbox" [checked]="draft.allLibraries" (change)="patch({ allLibraries: checkedOf($event) })">
                        <span>{{ 'groups.allLibraries' | t }}<span class="field-hint">{{ 'groups.allLibrariesHint' | t }}</span></span>
                    </label>
                    @if (!draft.allLibraries) {
                        <div class="checks">
                            @for (library of libraries(); track library.id) {
                                <label class="check">
                                    <input type="checkbox" [checked]="draft.libraryIds.includes(library.id)" (change)="toggle('libraryIds', library.id, checkedOf($event))">
                                    <span>{{ library.name }}</span>
                                </label>
                            }
                        </div>
                    }
                </fieldset>
                <fieldset class="field">
                    <legend class="field-label">{{ 'groups.members' | t }}</legend>
                    <div class="checks">
                        @for (user of users(); track user.id) {
                            <label class="check">
                                <input type="checkbox" [checked]="draft.memberIds.includes(user.id)" (change)="toggle('memberIds', user.id, checkedOf($event))">
                                <span>{{ user.displayName || user.userName }}</span>
                            </label>
                        } @empty {
                            <span class="field-hint">{{ 'users.none' | t }}</span>
                        }
                    </div>
                </fieldset>
                @if (error(); as error) {
                    <p class="form-error" role="alert">{{ error }}</p>
                }
                <div class="form-actions">
                    <button type="submit" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy()">{{ 'common.save' | t }}</button>
                    <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="draftState.set(null)">{{ 'common.cancel' | t }}</button>
                    @if (draft.id !== null && draft.builtIn === null) {
                        <button type="button" class="button danger" [title]="'groups.deleteHint' | t" [disabled]="busy()" (click)="remove(draft)">{{ 'common.delete' | t }}</button>
                    }
                </div>
            </form>
        }

        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.groups' | t">
            @for (group of groups(); track group.id) {
                <li class="row">
                    <button type="button" class="row-main" [title]="'groups.editHint' | t: { name: groupName(group) }" (click)="edit(group)">
                        <span class="row-title">{{ groupName(group) }}</span>
                        <span class="row-meta">{{ 'groups.summary' | t: { rights: group.rights.length, members: group.memberIds.length } }}</span>
                    </button>
                    @if (group.builtIn) {
                        <span class="badge">{{ 'groups.builtIn' | t }}</span>
                    }
                </li>
            }
        </ul>
    `,
})
export class GroupsSection implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);

    protected readonly groups = signal<GroupInfo[]>([]);
    protected readonly catalog = signal<RightInfo[]>([]);
    protected readonly libraries = signal<LibraryInfo[]>([]);
    protected readonly users = signal<UserInfo[]>([]);
    protected readonly draftState = signal<GroupDraft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly listError = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});

    /** The grantable rights by area, in the catalog's order (server rights belong to server administrators). */
    protected readonly areas = computed(() => {
        const areas: { name: string; rights: RightInfo[] }[] = [];
        for (const right of this.catalog().filter(r => r.scope !== 'server')) {
            let area = areas.find(a => a.name === right.area);
            if (!area)
                areas.push(area = { name: right.area, rights: [] });
            area.rights.push(right);
        }
        return areas;
    });

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    async ngOnInit(): Promise<void> {
        try {
            const [groups, catalog, libraries, users] = await Promise.all([this.api.groups(), this.api.rights(), this.api.libraries(), this.api.users()]);
            this.groups.set(groups);
            this.catalog.set(catalog);
            this.libraries.set(libraries);
            this.users.set(users);
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        }
    }

    protected groupName(group: GroupInfo): string {
        return group.builtIn ? this.i18n.t(`group.${group.builtIn}`) : group.name;
    }

    protected startNew(): void {
        this.open({ id: null, builtIn: null, name: '', allLibraries: true, rights: [], libraryIds: [], memberIds: [] });
    }

    protected edit(group: GroupInfo): void {
        this.open({
            id: group.id, builtIn: group.builtIn, name: group.name, allLibraries: group.allLibraries,
            rights: [...group.rights], libraryIds: [...group.libraryIds], memberIds: [...group.memberIds],
        });
    }

    protected patch(change: Partial<GroupDraft>): void {
        this.draftState.update(draft => draft && { ...draft, ...change });
    }

    protected toggle(list: 'rights' | 'libraryIds' | 'memberIds', value: string | number, on: boolean): void {
        this.draftState.update(draft => {
            if (!draft)
                return draft;
            const values = draft[list] as (string | number)[];
            return { ...draft, [list]: on ? [...values, value] : values.filter(v => v !== value) };
        });
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft)
            return;
        await this.run(async () => {
            const input = { name: draft.name.trim(), allLibraries: draft.allLibraries, rights: draft.rights, libraryIds: draft.libraryIds, memberIds: draft.memberIds };
            if (draft.id === null)
                await this.api.createGroup(input);
            else
                await this.api.updateGroup(draft.id, input);
            this.draftState.set(null);
            this.groups.set(await this.api.groups());
        });
    }

    protected async remove(draft: GroupDraft): Promise<void> {
        if (draft.id === null || !confirm(this.i18n.t('groups.deleteConfirm', { name: draft.name })))
            return;
        await this.run(async () => {
            await this.api.deleteGroup(draft.id!);
            this.draftState.set(null);
            this.groups.set(await this.api.groups());
        });
    }

    private open(draft: GroupDraft): void {
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
}
