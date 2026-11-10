import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookTagEdit, PickedTag } from '../core/models';
import { ToastService } from '../core/toast.service';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';

type TagState = 'all' | 'some' | 'none';

/** A row of the dialog: a tag the selection has (`had` of them) or one added here (`id` null when made by name). */
interface TagRow {
    id: number | null;
    name: string;
    color: string | null;
    had: number;
    state: TagState;
}

/**
 * The plain tags of selected books (stage 6, chunk 7; SFE's selection tags, agents/docs/features/tags.md): each tag
 * the selection has with a three-state box (all of them, some: left as it is, none), tags added with the shared picker
 * (Create too); Save gives the ticked ones to all and takes the cleared ones from all (`POST api/books/tags`).
 */
@Component({
    selector: 'lib-selection-tags',
    imports: [TranslatePipe, Sheet, TagPicker],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="(books().length === 1 ? 'tagMany.heading.one' : 'tagMany.heading') | t: { count: i18n.number(books().length) }"
                   (closed)="closed.emit()">
            @if (rows(); as rows) {
                <form id="selection-tags-form" class="selection-tags" novalidate (submit)="save($event)">
                    <p class="field-hint">{{ 'tagMany.hint' | t }}</p>
                    @if (rows.length) {
                        <ul class="selection-tag-rows">
                            @for (row of rows; track row.id ?? row.name; let i = $index) {
                                <li>
                                    <label class="check">
                                        <input type="checkbox" [checked]="row.state === 'all'" [indeterminate]="row.state === 'some'"
                                               [attr.aria-label]="label(row)" (change)="cycle(i)">
                                        <span class="tag-dot" [style.background]="row.color"></span>
                                        <span>{{ row.name }}</span>
                                        @if (row.state === 'some') {
                                            <span class="field-hint">{{ 'tagMany.some' | t: { count: i18n.number(row.had), total: i18n.number(total()) } }}</span>
                                        }
                                    </label>
                                </li>
                            }
                        </ul>
                    } @else {
                        <p class="field-hint">{{ 'tagMany.empty' | t }}</p>
                    }
                    <lib-tag-picker inputId="selection-tag-add" [label]="'tagMany.add' | t" [allowCreate]="true" [exclude]="ids()"
                                    (picked)="add($event)" (create)="create($event)" />
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                </form>
            } @else if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            } @else {
                <p class="field-hint" role="status">{{ 'tagMany.loading' | t }}</p>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="submit" form="selection-tags-form" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy() || !rows()">{{ 'common.save' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class SelectionTags implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);

    readonly books = input.required<number[]>();
    readonly saved = output<void>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly rows = signal<TagRow[] | null>(null);
    /** The selected books the caller may tag (the server's count). */
    protected readonly total = signal(0);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly ids = computed(() => (this.rows() ?? []).flatMap(row => row.id === null ? [] : [row.id]));

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        try {
            const state = await this.api.selectionTags(this.books());
            this.total.set(state.books);
            this.rows.set(state.tags.map(tag => ({
                id: tag.id, name: tag.name, color: tag.color, had: tag.books, state: tag.books >= state.books ? 'all' : 'some',
            })));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected label(row: TagRow): string {
        return this.i18n.t(`tagMany.state.${row.state}`, { name: row.name, count: this.i18n.number(row.had), total: this.i18n.number(this.total()) });
    }

    /** All → none → (some, when some had it to begin with) → all. */
    protected cycle(index: number): void {
        this.rows.update(rows => rows && rows.map((row, i) => {
            if (i !== index)
                return row;
            const partly = row.had > 0 && row.had < this.total();
            const next: TagState = row.state === 'all' ? 'none' : row.state === 'none' && partly ? 'some' : 'all';
            return { ...row, state: next };
        }));
    }

    protected add(tag: PickedTag): void {
        this.rows.update(rows => rows && (rows.some(r => r.id === tag.id) ? rows : [...rows, { id: tag.id, name: tag.name, color: tag.color, had: 0, state: 'all' }]));
    }

    protected create(name: string): void {
        const key = name.trim().toLocaleLowerCase();
        this.rows.update(rows => rows && (!key || rows.some(r => r.name.toLocaleLowerCase() === key) ? rows
            : [...rows, { id: null, name: name.trim(), color: null, had: 0, state: 'all' }]));
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const rows = this.rows() ?? [];
        const total = this.total();
        const add: BookTagEdit[] = rows.filter(r => r.state === 'all' && r.had < total).map(r => r.id !== null ? { id: r.id } : { name: r.name });
        const remove = rows.filter(r => r.state === 'none' && r.had > 0 && r.id !== null).map(r => r.id!);
        if (!add.length && !remove.length) {
            this.sheet()?.close();
            return;
        }
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.api.tagSelection(this.books(), { add, remove });
            this.toasts.show(this.i18n.t('tagMany.done'));
            this.saved.emit();
            this.sheet()?.close();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
