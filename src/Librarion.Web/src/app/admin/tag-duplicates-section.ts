import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ManagedField, ManagedFields, TagDuplicateItem, TagDuplicateTag } from '../core/models';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';

/** The plain tags in the field choice (fields go by key). */
const PLAIN = '';

/**
 * Administration › Catalog › Tags › Possible duplicates (stage 6 chunk 14, `tenant.tags`;
 * agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14): a field's tags that may be one (the
 * same words, sound alike, or a name and its initials), the groups with most books first. Keep one, merge the others
 * into it (each a tag merge, undone under Merges), or say they are not the same. Find again builds the groups as a run.
 */
@Component({
    selector: 'lib-tag-duplicates-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <label class="inline-field">
                <span class="field-label">{{ 'tagAdmin.field' | t }}</span>
                <select id="tag-duplicates-field" (change)="choose(valueOf($event))">
                    <option [value]="PLAIN" [selected]="fieldKey() === PLAIN">{{ 'tagDuplicates.plainTags' | t }}</option>
                    @for (field of fields(); track field.id) {
                        <option [value]="field.key" [selected]="fieldKey() === field.key">{{ fieldName(field) }}</option>
                    }
                </select>
            </label>
            <button type="button" class="button" [title]="'tagDuplicates.rebuildHint' | t" [disabled]="busy()" (click)="rebuild()">{{ 'duplicates.rebuild' | t }}</button>
        </div>
        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @if (!built() && !loading()) {
            <p class="field-hint">{{ 'tagDuplicates.notBuilt' | t }}</p>
        }
        <ul class="rows duplicate-groups" [attr.aria-label]="'tagDuplicates.list' | t" [attr.aria-busy]="loading()">
            @for (group of groups(); track group.id) {
                <li class="duplicate-group">
                    <p class="duplicate-reason">{{ 'tagDuplicates.level.' + group.level | t }}</p>
                    <fieldset class="tag-duplicate-tags">
                        <legend class="visually-hidden">{{ 'tagDuplicates.keep' | t }}</legend>
                        @for (tag of group.tags; track tag.id) {
                            <label class="check">
                                <input type="radio" [name]="'keep-' + group.id" [checked]="kept(group).id === tag.id" (change)="keep(group, tag)">
                                <span>{{ tag.name }} <span class="row-meta">· {{ 'tagDuplicates.books' + (tag.books === 1 ? '.one' : '') | t: { count: i18n.number(tag.books) } }}</span></span>
                            </label>
                        }
                    </fieldset>
                    <div class="duplicate-actions">
                        <button type="button" class="button primary" [title]="'tagDuplicates.mergeHint' | t" [disabled]="busy()" (click)="merge(group)">
                            {{ 'tagDuplicates.merge' | t: { name: kept(group).name } }}
                        </button>
                        <button type="button" class="button" [title]="'tagDuplicates.notHint' | t" [disabled]="busy()" (click)="notSame(group)">{{ 'tagDuplicates.not' | t }}</button>
                    </div>
                </li>
            } @empty {
                @if (!loading() && built()) {
                    <li class="empty"><p class="empty-text">{{ 'tagDuplicates.none' | t }}</p></li>
                }
            }
        </ul>
        @if (next()) {
            <button type="button" class="button" [disabled]="loading()" (click)="load(true)">{{ 'tagAdmin.more' | t }}</button>
        }
    `,
})
export class TagDuplicatesSection implements OnInit {
    protected readonly i18n = inject(I18n);
    private readonly api = inject(Api);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly watch = inject(TaskWatch);

    protected readonly PLAIN = PLAIN;
    protected readonly valueOf = valueOf;
    private readonly fieldsInfo = signal<ManagedFields | null>(null);
    protected readonly fields = computed(() => this.fieldsInfo()?.fields ?? []);
    protected readonly fieldKey = signal(PLAIN);
    protected readonly groups = signal<TagDuplicateItem[]>([]);
    protected readonly next = signal<string | null>(null);
    protected readonly built = signal(true);
    protected readonly loading = signal(true);
    protected readonly busy = signal(false);
    protected readonly listError = signal<string | null>(null);
    /** The tag kept of each group, by group id: the one with most books until chosen. */
    private readonly keptIds = signal<ReadonlyMap<number, number>>(new Map());

    private asked = 0;
    /** The rebuild this page started: the list is read again when it ends. */
    private waiting: number | null = null;

    constructor() {
        effect(() => {
            // The signal read first: an effect that read none would never run again.
            const ended = this.watch.ended();
            if (this.waiting !== null && ended.some(run => run.id === this.waiting)) {
                this.waiting = null;
                untracked(() => void this.load(false));
            }
        });
    }

    async ngOnInit(): Promise<void> {
        try {
            const info = await this.api.managedFields();
            this.fieldsInfo.set(info);
            // Authors first: where most look-alikes are.
            if (info.fields.some(f => f.key === 'author'))
                this.fieldKey.set('author');
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        }
        await this.load(false);
    }

    protected fieldName(field: ManagedField): string {
        return field.builtIn ? this.i18n.t(`fields.name.${field.key}`) : field.name;
    }

    protected choose(key: string): void {
        this.fieldKey.set(key);
        void this.load(false);
    }

    protected kept(group: TagDuplicateItem): TagDuplicateTag {
        const id = this.keptIds().get(group.id);
        return group.tags.find(tag => tag.id === id) ?? group.tags[0];
    }

    protected keep(group: TagDuplicateItem, tag: TagDuplicateTag): void {
        this.keptIds.update(kept => new Map(kept).set(group.id, tag.id));
    }

    async load(more: boolean): Promise<void> {
        const asked = ++this.asked;
        this.loading.set(true);
        this.listError.set(null);
        try {
            const page = await this.api.tagDuplicates(this.fieldKey() || null, more ? this.next() : null);
            if (asked !== this.asked)
                return;
            this.groups.set(more ? [...this.groups(), ...page.items] : page.items);
            this.next.set(page.next);
            this.built.set(page.built);
        } catch (error) {
            if (asked === this.asked)
                this.listError.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.asked)
                this.loading.set(false);
        }
    }

    /** The others merged into the kept tag, one tag merge each (a big one runs in the background, followed by the panel). */
    protected async merge(group: TagDuplicateItem): Promise<void> {
        const target = this.kept(group);
        const sources = group.tags.filter(tag => tag.id !== target.id);
        const ok = await this.confirm.ask({
            title: this.i18n.t('tagDuplicates.mergeTitle'),
            message: this.i18n.t('tagDuplicates.mergeConfirm', { names: sources.map(tag => tag.name).join(', '), name: target.name }),
            action: this.i18n.t('tagDuplicates.mergeAction'),
        });
        if (!ok)
            return;
        this.busy.set(true);
        this.listError.set(null);
        try {
            for (const source of sources) {
                const work = await this.api.mergeTag(source.id, target.id);
                if (work.runId !== null)
                    this.watch.follow(work.runId);
            }
            this.groups.update(groups => groups.filter(g => g.id !== group.id));
            this.toasts.show(this.i18n.t('tagDuplicates.merged', { name: target.name }));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async notSame(group: TagDuplicateItem): Promise<void> {
        this.busy.set(true);
        this.listError.set(null);
        try {
            await this.api.notSameTags(group.tags.map(tag => tag.id));
            this.groups.update(groups => groups.filter(g => g.id !== group.id));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async rebuild(): Promise<void> {
        this.busy.set(true);
        try {
            const { runId } = await this.api.buildTagDuplicates();
            this.waiting = runId;
            this.watch.follow(runId);
            this.toasts.show(this.i18n.t('tagDuplicates.rebuilding'));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
