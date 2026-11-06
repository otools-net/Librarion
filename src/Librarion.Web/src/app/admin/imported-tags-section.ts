import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import {
    IMPORT_SOURCES, IgnoredTagInfo, ImportSource, ImportedContext, ImportedField, ImportedStatus, ImportedSummary, ImportedTag, TagWork,
} from '../core/models';
import { formatMoment } from '../core/task-text';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { Icon } from '../shared/icon';
import { ImportedMap } from './imported-map';

/** At most this many tags in one change (`TagMerger.MaxBulk`). */
const MAX_SELECTED = 100;
const STATUSES: readonly ImportedStatus[] = ['review', 'reviewed', 'all'];

/**
 * Administration › Catalog › Imported tags (stage 6 chunk 9, agents/docs/features/imported-tags.md): the tags imports
 * give, of plain tags or one field, in one library by their books from the most, with the raw names that give each and
 * where they came from; how much the first 500 and the reviewed ones cover. Rows are chosen with their boxes, then kept
 * (right as they are), mapped onto another tag (merged) or ignored (imports skip their names, their imported links go);
 * Ignored lists the ignores, each undone with a click.
 */
@Component({
    selector: 'lib-imported-tags-section',
    imports: [TranslatePipe, Icon, ImportedMap],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @if (page() === 'ignored') {
            <ul class="rows" [attr.aria-label]="'adminPage.importedTags.ignored' | t" [attr.aria-busy]="loading()">
                @for (ignore of ignores(); track ignore.id) {
                    <li class="row merge-row">
                        <div class="row-main">
                            <span class="row-title">{{ ignore.tagName }}</span>
                            <span class="row-meta">{{ ignoreMeta(ignore) }}</span>
                        </div>
                        @if (!ignore.completed) {
                            <span class="badge danger">{{ 'tagAdmin.incomplete' | t }}</span>
                        }
                        @if (context()?.canChange) {
                            <button type="button" class="button" [title]="'importedTags.undoHint' | t" [disabled]="busy()" (click)="undo(ignore)">{{ 'tagAdmin.undo' | t }}</button>
                        }
                    </li>
                } @empty {
                    @if (!loading()) {
                        <li class="empty"><p class="empty-text">{{ 'importedTags.noIgnores' | t }}</p></li>
                    }
                }
            </ul>
            @if (ignoresMore()) {
                <button type="button" class="button" [disabled]="loading()" (click)="loadIgnores(true)">{{ 'tagAdmin.older' | t }}</button>
            }
        } @else if (context(); as context) {
            @if (!libraries().length) {
                <p class="empty-text">{{ 'importedTags.noLibrary' | t }}</p>
            } @else {
                <div class="toolbar">
                    @if (libraries().length > 1) {
                        <label class="inline-field">
                            <span class="field-label">{{ 'importedTags.library' | t }}</span>
                            <select id="imported-library" (change)="chooseLibrary(+valueOf($event))">
                                @for (library of libraries(); track library.id) {
                                    <option [value]="library.id" [selected]="library.id === library$()">{{ library.name }}</option>
                                }
                            </select>
                        </label>
                    }
                    <label class="inline-field">
                        <span class="field-label">{{ 'tagAdmin.field' | t }}</span>
                        <select id="imported-field" (change)="chooseField(valueOf($event))">
                            @for (field of fields(); track field.key) {
                                <option [value]="field.key ?? ''" [selected]="field.key === fieldKey()">{{ fieldName(field) }} ({{ counted(field) }})</option>
                            }
                        </select>
                    </label>
                    <label class="inline-field">
                        <span class="field-label">{{ 'importedTags.source' | t }}</span>
                        <select id="imported-source" (change)="chooseSource(valueOf($event))">
                            <option value="" [selected]="!source()">{{ 'importedTags.anySource' | t }}</option>
                            @for (kind of SOURCES; track kind) {
                                <option [value]="kind" [selected]="kind === source()">{{ 'importedTags.from.' + kind | t }}</option>
                            }
                        </select>
                    </label>
                    <label class="inline-field">
                        <span class="field-label">{{ 'importedTags.status' | t }}</span>
                        <select id="imported-status" (change)="chooseStatus(valueOf($event))">
                            @for (status of STATUSES; track status) {
                                <option [value]="status" [selected]="status === status$()">{{ 'importedTags.status.' + status | t }}</option>
                            }
                        </select>
                    </label>
                </div>
                @if (summary(); as summary) {
                    <p class="field-hint imported-summary">{{ summaryText(summary) }}</p>
                }

                @if (selected().size) {
                    <div class="selection-bar imported-bar" role="region" [attr.aria-label]="'importedTags.bar' | t">
                        <button type="button" class="button icon-only" [attr.aria-label]="'select.clear' | t" [title]="'select.clearHint' | t" (click)="clear()">
                            <svg libIcon="close"></svg>
                        </button>
                        <span class="selection-count">{{ 'select.count' | t: { count: i18n.number(selected().size) } }}</span>
                        @if (status$() !== 'reviewed') {
                            <button type="button" class="button" [title]="'importedTags.keepHint' | t" [disabled]="busy()" (click)="keep(true)">{{ 'importedTags.keep' | t }}</button>
                        } @else {
                            <button type="button" class="button" [title]="'importedTags.unkeepHint' | t" [disabled]="busy()" (click)="keep(false)">{{ 'importedTags.unkeep' | t }}</button>
                        }
                        @if (context.canMap) {
                            <button type="button" class="button" [title]="'importedTags.mapHint' | t" [disabled]="busy()" (click)="mapping.set(chosen())">{{ 'importedTags.mapTo' | t }}</button>
                        }
                        <button type="button" class="button danger" [title]="'importedTags.ignoreHint' | t" [disabled]="busy()" (click)="ignore()">{{ 'importedTags.ignore' | t }}</button>
                    </div>
                }

                <ul class="rows imported-rows" [attr.aria-label]="'importedTags.list' | t" [attr.aria-busy]="loading()">
                    @for (tag of tags(); track tag.id) {
                        <li class="row" [class.selected]="selected().has(tag.id)">
                            @if (context.canChange) {
                                <input type="checkbox" class="row-check" [attr.aria-label]="'importedTags.choose' | t: { name: tag.name }"
                                       [checked]="selected().has(tag.id)" (change)="toggle(tag)">
                            }
                            <div class="row-main">
                                <span class="row-title"><span class="tag-dot" [style.background]="tag.color"></span>{{ tag.name }}</span>
                                <span class="row-meta">{{ meta(tag) }}</span>
                            </div>
                            @if (tag.reviewed) {
                                <span class="badge">{{ 'importedTags.reviewedBadge' | t }}</span>
                            }
                        </li>
                    } @empty {
                        @if (!loading()) {
                            <li class="empty"><p class="empty-text">{{ 'importedTags.none.' + status$() | t }}</p></li>
                        }
                    }
                </ul>
                @if (next()) {
                    <button type="button" class="button" [disabled]="loading()" (click)="load(true)">{{ 'tagAdmin.more' | t }}</button>
                }
            }
        }

        @if (mapping(); as tags) {
            @if (field(); as field) {
                <lib-imported-map [tags]="tags" [field]="field" (done)="mapped($event)" (closed)="mapping.set(null)" />
            }
        }
    `,
})
export class ImportedTagsSection implements OnInit {
    protected readonly i18n = inject(I18n);
    private readonly api = inject(Api);
    private readonly libraryContext = inject(LibraryContext);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly watch = inject(TaskWatch);

    /** `names` (the list) or `ignored`. */
    readonly page = input('names');

    protected readonly SOURCES = IMPORT_SOURCES;
    protected readonly STATUSES = STATUSES;
    protected readonly context = signal<ImportedContext | null>(null);
    protected readonly libraries = computed(() => {
        const allowed = new Set(this.context()?.libraries ?? []);
        return this.libraryContext.libraries().filter(l => allowed.has(l.id));
    });
    protected readonly library$ = signal<number | null>(null);
    protected readonly fields = signal<ImportedField[]>([]);
    protected readonly fieldKey = signal<string | null>(null);
    protected readonly field = computed(() => this.fields().find(f => f.key === this.fieldKey()) ?? null);
    protected readonly source = signal<ImportSource | null>(null);
    protected readonly status$ = signal<ImportedStatus>('review');
    protected readonly tags = signal<ImportedTag[]>([]);
    protected readonly next = signal<string | null>(null);
    protected readonly summary = signal<ImportedSummary | null>(null);
    protected readonly selected = signal<ReadonlySet<number>>(new Set());
    protected readonly mapping = signal<ImportedTag[] | null>(null);
    protected readonly ignores = signal<IgnoredTagInfo[]>([]);
    protected readonly ignoresMore = signal(false);
    protected readonly loading = signal(true);
    protected readonly busy = signal(false);
    protected readonly listError = signal<string | null>(null);

    private asked = 0;
    /** Runs this page started: the list is read again when one ends (the panel shows it). */
    private readonly waiting = new Set<number>();

    protected readonly valueOf = valueOf;

    constructor() {
        effect(() => {
            const done = this.watch.ended().filter(run => this.waiting.has(run.id));
            if (!done.length)
                return;
            done.forEach(run => this.waiting.delete(run.id));
            untracked(() => void (this.page() === 'ignored' ? this.loadIgnores(false) : this.reload()));
        });
    }

    async ngOnInit(): Promise<void> {
        try {
            this.context.set(await this.api.importedContext());
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
            this.loading.set(false);
            return;
        }

        if (this.page() === 'ignored') {
            await this.loadIgnores(false);
            return;
        }
        if (!this.libraryContext.libraries().length)
            await this.libraryContext.load();
        const current = this.libraryContext.current()?.id;
        const libraries = this.libraries();
        this.library$.set(libraries.find(l => l.id === current)?.id ?? libraries[0]?.id ?? null);
        try {
            this.fields.set(await this.api.importedFields());
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        }
        await this.reload();
    }

    protected fieldName(field: ImportedField): string {
        if (field.key === null)
            return this.i18n.t('importedTags.plainTags');
        return field.builtIn ? this.i18n.t(`fields.name.${field.key}`) : field.name;
    }

    protected counted(field: ImportedField): string {
        return field.capped ? this.i18n.t('tagAdmin.atLeast', { count: this.i18n.number(field.tags) }) : this.i18n.number(field.tags);
    }

    protected meta(tag: ImportedTag): string {
        const parts = [this.i18n.t('tagAdmin.books', { count: this.i18n.number(tag.books) })];
        const sources = [...new Set(tag.names.map(n => this.i18n.t(`importedTags.from.${n.source}`)))];
        if (sources.length)
            parts.push(this.i18n.t('importedTags.fromSources', { sources: sources.join(', ') }));
        // The raw names that differ from the tag's own (case aside): what an import wrote.
        const other = [...new Set(tag.names.map(n => n.name).filter(n => n.toLocaleLowerCase() !== tag.name.toLocaleLowerCase()))];
        if (other.length)
            parts.push(this.i18n.t('importedTags.as', { names: other.map(n => `“${n}”`).join(', ') }));
        return parts.join(' · ');
    }

    protected summaryText(summary: ImportedSummary): string {
        const share = (part: number) => summary.links ? Math.round(part * 100 / summary.links) : 0;
        return this.i18n.t('importedTags.summary', {
            tags: this.i18n.number(summary.tags), links: this.i18n.number(summary.links), top: this.i18n.number(Math.min(summary.topCount, summary.tags)),
            topShare: share(summary.top), reviewed: share(summary.reviewed),
        });
    }

    protected ignoreMeta(ignore: IgnoredTagInfo): string {
        const names = ignore.names.map(n => `“${n}”`).join(', ');
        return this.i18n.t('importedTags.ignoreMeta', { names, links: this.i18n.number(ignore.links), time: formatMoment(ignore.created) });
    }

    protected chooseLibrary(id: number): void {
        this.library$.set(id);
        void this.reload();
    }

    protected chooseField(key: string): void {
        this.fieldKey.set(key || null);
        void this.reload();
    }

    protected chooseSource(kind: string): void {
        this.source.set((kind || null) as ImportSource | null);
        void this.reload();
    }

    protected chooseStatus(status: string): void {
        this.status$.set(status as ImportedStatus);
        this.clear();
        void this.load(false);
    }

    protected toggle(tag: ImportedTag): void {
        const next = new Set(this.selected());
        if (next.has(tag.id))
            next.delete(tag.id);
        else if (next.size < MAX_SELECTED)
            next.add(tag.id);
        else
            this.toasts.show(this.i18n.t('importedTags.tooMany', { count: MAX_SELECTED }));
        this.selected.set(next);
    }

    protected clear(): void {
        this.selected.set(new Set());
    }

    protected chosen(): ImportedTag[] {
        return this.tags().filter(t => this.selected().has(t.id));
    }

    protected async keep(reviewed: boolean): Promise<void> {
        const ids = [...this.selected()];
        this.busy.set(true);
        this.listError.set(null);
        try {
            await this.api.reviewImported(ids, reviewed);
            this.toasts.show(this.i18n.t(reviewed ? 'importedTags.kept' : 'importedTags.unkept', { count: this.i18n.number(ids.length) }));
            await this.reload();
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async ignore(): Promise<void> {
        const tags = this.chosen();
        const ok = await this.confirm.ask({
            title: this.i18n.t('importedTags.ignoreTitle', { count: this.i18n.number(tags.length) }),
            message: this.i18n.t('importedTags.ignoreConfirm', {
                names: tags.slice(0, 5).map(t => `“${t.name}”`).join(', ') + (tags.length > 5 ? '…' : ''),
                books: this.i18n.number(tags.reduce((sum, t) => sum + t.books, 0)),
            }),
            action: this.i18n.t('importedTags.ignore'), danger: true,
        });
        if (!ok)
            return;
        this.busy.set(true);
        this.listError.set(null);
        try {
            this.report(await this.api.ignoreImported(tags.map(t => t.id)), this.i18n.t('importedTags.ignored', { count: this.i18n.number(tags.length) }));
            await this.reload();
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async mapped(work: TagWork): Promise<void> {
        this.report(work, this.i18n.t('importedTags.mapped'));
        await this.reload();
    }

    protected async undo(ignore: IgnoredTagInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('importedTags.undoTitle'),
            message: this.i18n.t('importedTags.undoConfirm', { name: ignore.tagName, links: this.i18n.number(ignore.links) }),
            action: this.i18n.t('tagAdmin.undo'),
        });
        if (!ok)
            return;
        this.busy.set(true);
        this.listError.set(null);
        try {
            this.report(await this.api.undoIgnore(ignore.id), this.i18n.t('tagAdmin.undoneToast', { name: ignore.tagName }));
            await this.loadIgnores(false);
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    async loadIgnores(more: boolean): Promise<void> {
        this.loading.set(true);
        try {
            const before = more ? this.ignores().at(-1)?.id : undefined;
            const page = await this.api.ignoredTags(before);
            this.ignores.set(more ? [...this.ignores(), ...page] : page);
            this.ignoresMore.set(page.length === 50);
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.loading.set(false);
        }
    }

    protected async load(more: boolean): Promise<void> {
        const library = this.library$();
        if (library === null)
            return;
        const asked = ++this.asked;
        this.loading.set(true);
        this.listError.set(null);
        try {
            const page = await this.api.importedTags({
                library, field: this.fieldKey(), source: this.source(), status: this.status$(), after: more ? this.next() : null, limit: 50,
            });
            if (asked !== this.asked)
                return;
            this.tags.set(more ? [...this.tags(), ...page.items] : page.items);
            this.next.set(page.next);
        } catch (error) {
            if (asked === this.asked)
                this.listError.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.asked)
                this.loading.set(false);
        }
    }

    /** The list and its coverage line again (another library, field or source, or after a change). */
    private async reload(): Promise<void> {
        this.clear();
        const library = this.library$();
        if (library === null) {
            this.loading.set(false);
            return;
        }
        const summary = this.api.importedSummary(library, this.fieldKey(), this.source()).then(s => this.summary.set(s), () => this.summary.set(null));
        await Promise.all([this.load(false), summary]);
    }

    private report(work: TagWork, done: string): void {
        if (work.runId !== null) {
            this.waiting.add(work.runId);
            this.watch.follow(work.runId);
            this.toasts.show(this.i18n.t('tagAdmin.running', { count: this.i18n.number(work.links) }));
        } else {
            this.toasts.show(done);
        }
    }
}
