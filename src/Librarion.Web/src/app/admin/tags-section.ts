import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ManagedField, ManagedFields, ManagedTag, MergeLogInfo, TagDefinition, TagWork } from '../core/models';
import { formatMoment } from '../core/task-text';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { Icon } from '../shared/icon';
import { TagEditor, TagEditorTarget } from './tag-editor';
import { MergeSource, TagMerge } from './tag-merge';

/** The plain tags in the field chooser (no field key). */
const PLAIN = '';

/**
 * Administration › Catalog › Tags (stage 6, D-6.2; port of SFE's tag manager): the plain tags or one field's values,
 * every one whether it has books or not, by name a page at a time or found by what is typed (never a field loaded
 * whole: 650k authors at 2M books); each with its books and how many tags are under it. A row opens the editor;
 * Delete and Merge say how many books change first; a big change runs in the background (the progress panel follows
 * it). Merges lists the merges, newest first, each undone with a click while nothing later stands in the way.
 */
@Component({
    selector: 'lib-tags-section',
    imports: [TranslatePipe, Icon, TagEditor, TagMerge],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (page() === 'merges') {
            @if (listError(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <ul class="rows" [attr.aria-label]="'adminPage.tags.merges' | t" [attr.aria-busy]="loading()">
                @for (merge of merges(); track merge.id) {
                    <li class="row merge-row">
                        <div class="row-main">
                            <span class="row-title">{{ merge.sourceName }} → {{ merge.targetName }}</span>
                            <span class="row-meta">{{ 'tagAdmin.mergeMeta' | t: { kind: ('tagAdmin.mergeKind.' + merge.kind | t), links: i18n.number(merge.links), time: moment(merge.created) } }}</span>
                        </div>
                        @if (merge.undone) {
                            <span class="badge">{{ 'tagAdmin.undone' | t }}</span>
                        } @else {
                            @if (!merge.completed) {
                                <span class="badge danger">{{ 'tagAdmin.incomplete' | t }}</span>
                            }
                            <button type="button" class="button" [title]="'tagAdmin.undoHint' | t" [disabled]="busy()" (click)="undo(merge)">{{ 'tagAdmin.undo' | t }}</button>
                        }
                    </li>
                } @empty {
                    @if (!loading()) {
                        <li class="empty"><p class="empty-text">{{ 'tagAdmin.noMerges' | t }}</p></li>
                    }
                }
            </ul>
            @if (mergesMore()) {
                <button type="button" class="button" [disabled]="loading()" (click)="loadMerges(true)">{{ 'tagAdmin.older' | t }}</button>
            }
        } @else {
            <div class="toolbar">
                <label class="inline-field">
                    <span class="field-label">{{ 'tagAdmin.field' | t }}</span>
                    <select id="tags-field" (change)="choose(valueOf($event))">
                        <option [value]="PLAIN" [selected]="fieldKey() === PLAIN">{{ 'tagAdmin.plainTags' | t: { count: counted(fieldsInfo()?.plainTags ?? 0, fieldsInfo()?.plainCapped ?? false) } }}</option>
                        @for (field of fields(); track field.id) {
                            <option [value]="field.key" [selected]="fieldKey() === field.key">{{ fieldName(field) }} ({{ counted(field.values, field.capped) }})</option>
                        }
                    </select>
                </label>
                <input type="search" class="toolbar-search" autocomplete="off" [placeholder]="'tagAdmin.findPlaceholder' | t" [attr.aria-label]="'tagAdmin.find' | t"
                       [value]="query()" (input)="find(valueOf($event))">
                <button type="button" class="button primary" [title]="'tagAdmin.newHint' | t" (click)="openEditor({ id: null, field: field() })">
                    <svg libIcon="plus"></svg>{{ (field() ? 'tagAdmin.newValue' : 'tagAdmin.new') | t }}
                </button>
                <button type="button" class="button" [title]="'tagAdmin.newFieldHint' | t" (click)="openEditor({ id: null, field: null, newField: true })">{{ 'tagAdmin.newField' | t }}</button>
                @if (field(); as field) {
                    <button type="button" class="button" [title]="'tagAdmin.editFieldHint' | t" (click)="openEditor({ id: field.id, field: null })">{{ 'tagAdmin.editField' | t }}</button>
                    @if (!field.builtIn) {
                        <button type="button" class="button" [title]="'tagAdmin.mergeFieldHint' | t" (click)="merging.set({ tag: null, field })">{{ 'tagAdmin.mergeField' | t }}</button>
                    }
                }
            </div>

            @if (listError(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <ul class="rows tag-rows" [attr.aria-label]="'tagAdmin.list' | t" [attr.aria-busy]="loading()">
                @for (tag of tags(); track tag.id) {
                    <li class="row">
                        <button type="button" class="row-main" [title]="'tagAdmin.editHint' | t: { name: tag.name }" (click)="openEditor({ id: tag.id, field: field() })">
                            <span class="row-title"><span class="tag-dot" [style.background]="tag.color"></span>{{ tag.name }}</span>
                            <span class="row-meta">{{ meta(tag) }}</span>
                        </button>
                        @if (tag.isHidden) {
                            <span class="badge">{{ 'tagAdmin.hiddenBadge' | t }}</span>
                        }
                        @if (tag.isCategory) {
                            <span class="badge">{{ 'tagAdmin.categoryBadge' | t }}</span>
                        }
                        @if (tag.builtIn) {
                            <span class="badge">{{ 'tagAdmin.builtIn' | t }}</span>
                        }
                    </li>
                } @empty {
                    @if (!loading()) {
                        <li class="empty"><p class="empty-text">{{ (query() ? 'tagAdmin.noneFound' : 'tagAdmin.none') | t }}</p></li>
                    }
                }
            </ul>
            @if (capped()) {
                <p class="field-hint">{{ 'tagAdmin.capped' | t: { count: tags().length } }}</p>
            }
            @if (next()) {
                <button type="button" class="button" [disabled]="loading()" (click)="load(true)">{{ 'tagAdmin.more' | t }}</button>
            }
        }

        @if (editing(); as target) {
            <lib-tag-editor [target]="target" (saved)="saved($event)" (closed)="editing.set(null)"
                            (remove)="remove($event)" (merge)="startMerge($event)" />
        }
        @if (merging(); as source) {
            <lib-tag-merge [source]="source" [fields]="fields()" (done)="merged($event)" (closed)="merging.set(null)" />
        }
    `,
})
export class TagsSection implements OnInit {
    protected readonly i18n = inject(I18n);
    private readonly api = inject(Api);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly watch = inject(TaskWatch);

    /** `tags` (the list) or `merges`. */
    readonly page = input('tags');

    protected readonly PLAIN = PLAIN;
    protected readonly fieldsInfo = signal<ManagedFields | null>(null);
    protected readonly fields = computed(() => this.fieldsInfo()?.fields ?? []);
    protected readonly fieldKey = signal(PLAIN);
    protected readonly field = computed(() => this.fields().find(f => f.key === this.fieldKey()) ?? null);
    protected readonly query = signal('');
    protected readonly tags = signal<ManagedTag[]>([]);
    protected readonly next = signal<string | null>(null);
    protected readonly capped = signal(false);
    protected readonly loading = signal(true);
    protected readonly busy = signal(false);
    protected readonly listError = signal<string | null>(null);
    protected readonly editing = signal<TagEditorTarget | null>(null);
    protected readonly merging = signal<MergeSource | null>(null);
    protected readonly merges = signal<MergeLogInfo[]>([]);
    protected readonly mergesMore = signal(false);

    private timer: ReturnType<typeof setTimeout> | null = null;
    private asked = 0;

    protected readonly valueOf = valueOf;
    protected readonly moment = formatMoment;

    async ngOnInit(): Promise<void> {
        if (this.page() === 'merges') {
            await this.loadMerges(false);
            return;
        }
        await Promise.all([this.loadFields(), this.load(false)]);
    }

    protected fieldName(field: ManagedField): string {
        return field.builtIn ? this.i18n.t(`fields.name.${field.key}`) : field.name;
    }

    protected counted(count: number, capped: boolean): string {
        return capped ? this.i18n.t('tagAdmin.atLeast', { count: this.i18n.number(count) }) : this.i18n.number(count);
    }

    protected meta(tag: ManagedTag): string {
        const books = this.i18n.t('tagAdmin.books', { count: this.i18n.number(tag.books) });
        return tag.children ? `${books} · ${this.i18n.t('tagAdmin.children', { count: this.i18n.number(tag.children) })}` : books;
    }

    protected choose(key: string): void {
        this.fieldKey.set(key);
        this.query.set('');
        void this.load(false);
    }

    protected find(text: string): void {
        this.query.set(text);
        if (this.timer)
            clearTimeout(this.timer);
        this.timer = setTimeout(() => void this.load(false), 250);
    }

    protected async load(more: boolean): Promise<void> {
        const asked = ++this.asked;
        this.loading.set(true);
        this.listError.set(null);
        try {
            const page = await this.api.managedTags(this.fieldKey() || null, { q: this.query().trim() || null, after: more ? this.next() : null, limit: 50 });
            if (asked !== this.asked)
                return;
            this.tags.set(more ? [...this.tags(), ...page.items] : page.items);
            this.next.set(page.next);
            this.capped.set(page.capped);
        } catch (error) {
            if (asked === this.asked)
                this.listError.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.asked)
                this.loading.set(false);
        }
    }

    protected openEditor(target: TagEditorTarget): void {
        this.editing.set(target);
    }

    protected async saved(tag: TagDefinition): Promise<void> {
        this.toasts.show(this.i18n.t('tagAdmin.saved', { name: tag.name }));
        // A new field is chosen at once, its values to come.
        if (tag.fieldKey && !this.fields().some(f => f.key === tag.fieldKey)) {
            await this.loadFields();
            this.choose(tag.fieldKey);
            return;
        }
        await Promise.all([this.loadFields(), this.load(false)]);
    }

    protected async remove(tag: TagDefinition): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('tagAdmin.deleteTitle', { name: tag.name }),
            message: this.i18n.t(tag.books ? 'tagAdmin.deleteConfirm' : 'tagAdmin.deleteConfirmEmpty', { name: tag.name, count: this.i18n.number(tag.books) }),
            action: this.i18n.t('common.delete'), danger: true,
        });
        if (!ok)
            return;
        await this.work(() => this.api.deleteTag(tag.id), this.i18n.t('tagAdmin.deleted', { name: tag.name }));
        this.editing.set(null);
    }

    protected startMerge(tag: TagDefinition): void {
        this.editing.set(null);
        this.merging.set({ tag: { id: tag.id, name: tag.name, books: tag.books }, field: tag.fieldKey ? null : this.field() });
    }

    protected async merged(work: TagWork): Promise<void> {
        this.report(work, this.i18n.t('tagAdmin.merged'));
        if (this.merging()?.tag === null)
            this.fieldKey.set(PLAIN);
        await Promise.all([this.loadFields(), this.load(false)]);
    }

    protected async undo(merge: MergeLogInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('tagAdmin.undoTitle'),
            message: this.i18n.t('tagAdmin.undoConfirm', { source: merge.sourceName, target: merge.targetName, links: this.i18n.number(merge.links) }),
            action: this.i18n.t('tagAdmin.undo'),
        });
        if (!ok)
            return;
        // A refusal stays on the page: the list is read again only once it is undone.
        if (await this.work(() => this.api.undoMerge(merge.id), this.i18n.t('tagAdmin.undoneToast', { name: merge.sourceName })))
            await this.loadMerges(false);
    }

    async loadMerges(more: boolean): Promise<void> {
        this.loading.set(true);
        this.listError.set(null);
        try {
            const before = more ? this.merges().at(-1)?.id : undefined;
            const page = await this.api.merges(before);
            this.merges.set(more ? [...this.merges(), ...page] : page);
            this.mergesMore.set(page.length === 50);
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.loading.set(false);
        }
    }

    private async loadFields(): Promise<void> {
        try {
            this.fieldsInfo.set(await this.api.managedFields());
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        }
    }

    /** A change that is done when it answers, or a run the progress panel follows; false when it was refused. */
    private async work(call: () => Promise<TagWork>, done: string): Promise<boolean> {
        this.busy.set(true);
        this.listError.set(null);
        try {
            this.report(await call(), done);
            if (this.page() !== 'merges')
                await Promise.all([this.loadFields(), this.load(false)]);
            return true;
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
            return false;
        } finally {
            this.busy.set(false);
        }
    }

    private report(work: TagWork, done: string): void {
        if (work.runId !== null) {
            this.watch.follow(work.runId);
            this.toasts.show(this.i18n.t('tagAdmin.running', { count: this.i18n.number(work.links) }));
        } else {
            this.toasts.show(done);
        }
    }
}
