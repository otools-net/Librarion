import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookDetail, BookEdit, BookTagEdit, PickedTag } from '../core/models';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';
import { FieldCatalog } from '../tags/field-catalog';

/** A value in the editor: an existing tag (`id`), or one to make by `name` on save. */
interface DraftTag {
    id: number | null;
    name: string;
    number: number | null;
}

interface DraftField {
    field: string | null;
    tags: DraftTag[];
}

interface BookDraft {
    title: string;
    description: string;
    rating: number;
    fields: DraftField[];
}

/** Fields shown first, in this order, when the book has them or not (the others when it has values of them, or added). */
const FIRST_FIELDS = ['author', 'series'];

/**
 * A book's editor on its page (stage 6, chunk 6, D-6.3; agents/docs/features/book-page.md): with `canEdit` its title,
 * description, rating and fields (each value chosen with the shared picker scoped to the field, or made by name; the
 * order kept, by drag or Move up / down; a series' number in place), with `canTag` its plain tags. Only what changed is
 * sent, so values nobody touched stay the import's.
 */
@Component({
    selector: 'lib-book-editor',
    imports: [TranslatePipe, Sheet, Icon, TagPicker],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'bookEdit.heading' | t" [subtitle]="subtitle()" [wide]="true" (closed)="closed.emit()">
            @if (draft(); as draft) {
                <form id="book-form" class="book-editor" novalidate (submit)="save($event)">
                    <p class="field-hint">{{ 'bookEdit.keeps' | t }}</p>
                    @if (book().canEdit) {
                        <div class="field">
                            <label class="field-label" for="book-title-input">{{ 'bookEdit.title' | t }}</label>
                            <input id="book-title-input" type="text" autocomplete="off" [placeholder]="book().fileName ?? ''" [value]="draft.title"
                                   (input)="patch({ title: valueOf($event) })">
                            <span class="field-hint">{{ 'bookEdit.titleHint' | t }}</span>
                            @if (errors()['title']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                    }
                    @for (group of shownFields(); track group.field) {
                        <fieldset class="field book-edit-field">
                            <legend class="field-label">{{ fieldTitle(group.field) }}</legend>
                            @if (group.field === null) {
                                @if (group.tags.length) {
                                    <ul class="tag-chips">
                                        @for (tag of group.tags; track tag.id ?? tag.name; let i = $index) {
                                            <li class="chip">
                                                <span>{{ tag.name }}</span>
                                                <button type="button" class="icon-button" [attr.aria-label]="'bookEdit.remove' | t: { name: tag.name }"
                                                        [title]="'bookEdit.remove' | t: { name: tag.name }" (click)="remove(group.field, i)">
                                                    <svg libIcon="close"></svg>
                                                </button>
                                            </li>
                                        }
                                    </ul>
                                }
                            } @else if (group.tags.length) {
                                <ol class="book-edit-values" [attr.data-field]="group.field">
                                    @for (tag of group.tags; track tag.id ?? tag.name; let i = $index, first = $first, last = $last) {
                                        <li class="book-edit-value" [class.dragging]="dragging() === group.field + ':' + i">
                                            @if (group.tags.length > 1) {
                                                <span class="book-edit-handle" role="img" [attr.aria-label]="'bookEdit.drag' | t: { name: tag.name }"
                                                      [title]="'bookEdit.drag' | t: { name: tag.name }" (pointerdown)="dragStart($event, group.field, i)">
                                                    <svg libIcon="grip"></svg>
                                                </span>
                                            }
                                            <span class="book-edit-name">{{ tag.name }}</span>
                                            @if (group.field === 'series') {
                                                <input class="book-edit-number" type="number" inputmode="decimal" min="0" step="any"
                                                       [attr.aria-label]="'bookEdit.number' | t: { name: tag.name }" [title]="'bookEdit.number' | t: { name: tag.name }"
                                                       [value]="tag.number ?? ''" (input)="number(group.field, i, valueOf($event))">
                                            }
                                            @if (group.tags.length > 1) {
                                                <button type="button" class="icon-button" [disabled]="first" [attr.aria-label]="'bookEdit.moveUp' | t: { name: tag.name }"
                                                        [title]="'bookEdit.moveUp' | t: { name: tag.name }" (click)="move(group.field, i, -1)">
                                                    <svg libIcon="chevron-up"></svg>
                                                </button>
                                                <button type="button" class="icon-button" [disabled]="last" [attr.aria-label]="'bookEdit.moveDown' | t: { name: tag.name }"
                                                        [title]="'bookEdit.moveDown' | t: { name: tag.name }" (click)="move(group.field, i, 1)">
                                                    <svg libIcon="chevron-down"></svg>
                                                </button>
                                            }
                                            <button type="button" class="icon-button" [attr.aria-label]="'bookEdit.remove' | t: { name: tag.name }"
                                                    [title]="'bookEdit.remove' | t: { name: tag.name }" (click)="remove(group.field, i)">
                                                <svg libIcon="close"></svg>
                                            </button>
                                        </li>
                                    }
                                </ol>
                            } @else {
                                <p class="field-hint">{{ 'bookEdit.none' | t }}</p>
                            }
                            <lib-tag-picker [inputId]="'book-add-' + (group.field ?? 'tag')" [field]="group.field" [allowCreate]="true"
                                            [label]="group.field === null ? ('bookEdit.addTag' | t) : ('bookEdit.add' | t: { field: fieldTitle(group.field) })"
                                            [exclude]="ids(group)" (picked)="add(group.field, $event)" (create)="create(group.field, $event)" />
                        </fieldset>
                    }
                    @if (book().canEdit && addable().length) {
                        <div class="field">
                            <label class="field-label" for="book-add-field">{{ 'bookEdit.addField' | t }}</label>
                            <select id="book-add-field" [title]="'bookEdit.addFieldHint' | t" (change)="addField($event)">
                                <option value="" selected>…</option>
                                @for (key of addable(); track key) {
                                    <option [value]="key">{{ catalog.title(key) }}</option>
                                }
                            </select>
                        </div>
                    }
                    @if (book().canEdit) {
                        <div class="field">
                            <label class="field-label" for="book-description-input">{{ 'bookEdit.description' | t }}</label>
                            <textarea id="book-description-input" rows="6" [value]="draft.description" (input)="patch({ description: valueOf($event) })"></textarea>
                            @if (errors()['description']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <label class="field-label" for="book-rating-input">{{ 'bookEdit.rating' | t }}</label>
                            <select id="book-rating-input" (change)="patch({ rating: +valueOf($event) })">
                                @for (value of ratings; track value) {
                                    <option [value]="value" [selected]="draft.rating === value">
                                        {{ value === 0 ? ('bookEdit.noRating' | t) : ('bookEdit.stars' | t: { stars: i18n.number(value / 2) }) }}
                                    </option>
                                }
                            </select>
                            @if (errors()['rating']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                    }
                    @for (key of otherErrors(); track key) {
                        <p class="form-error" role="alert">{{ errors()[key] }}</p>
                    }
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                </form>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="submit" form="book-form" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy()">{{ 'common.save' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class BookEditor implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly catalog = inject(FieldCatalog);

    readonly book = input.required<BookDetail>();
    readonly saved = output<BookDetail>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly draftState = signal<BookDraft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly dragging = signal<string | null>(null);
    protected readonly ratings = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private original: BookDraft | null = null;

    protected readonly subtitle = computed(() => this.book().title || this.book().fileName);
    /** The fields the user may change, as the draft has them: metadata's with `canEdit`, the plain tags with `canTag`. */
    protected readonly shownFields = computed(() => {
        const book = this.book();
        return (this.draft()?.fields ?? []).filter(group => group.field === null ? book.canTag : book.canEdit);
    });
    /** Fields of the catalog the draft doesn't show yet. */
    protected readonly addable = computed(() => {
        const shown = new Set(this.draft()?.fields.map(f => f.field) ?? []);
        return this.catalog.fields().map(f => f.key).filter(key => !shown.has(key));
    });
    protected readonly otherErrors = computed(() => Object.keys(this.errors()).filter(key => !['title', 'description', 'rating'].includes(key)));

    protected readonly valueOf = valueOf;

    constructor() {
        effect(() => this.sheet()?.open());
    }

    ngOnInit(): void {
        void this.catalog.load();
        const book = this.book();
        const fields: DraftField[] = FIRST_FIELDS.map(field => ({ field, tags: [] }));
        for (const tag of book.tags) {
            let group = fields.find(f => f.field === tag.field);
            if (!group)
                fields.push(group = { field: tag.field, tags: [] });
            group.tags.push({ id: tag.id, name: tag.name, number: tag.number });
        }
        if (!fields.some(f => f.field === null))
            fields.push({ field: null, tags: [] });
        // The plain tags last.
        fields.push(...fields.splice(fields.findIndex(f => f.field === null), 1));
        const draft: BookDraft = { title: book.title ?? '', description: book.description ?? '', rating: book.rating ?? 0, fields };
        this.original = structuredClone(draft);
        this.draftState.set(draft);
    }

    protected fieldTitle(field: string | null): string {
        return field === null ? this.i18n.t('bookEdit.tags') : this.catalog.title(field);
    }

    protected ids(group: DraftField): number[] {
        return group.tags.flatMap(tag => tag.id === null ? [] : [tag.id]);
    }

    protected patch(change: Partial<BookDraft>): void {
        this.draftState.update(draft => draft && { ...draft, ...change });
    }

    private update(field: string | null, change: (tags: DraftTag[]) => DraftTag[]): void {
        this.draftState.update(draft => draft && {
            ...draft, fields: draft.fields.map(group => group.field === field ? { ...group, tags: change([...group.tags]) } : group),
        });
    }

    protected add(field: string | null, tag: PickedTag): void {
        this.update(field, tags => tags.some(t => t.id === tag.id) ? tags : [...tags, { id: tag.id, name: tag.name, number: null }]);
    }

    protected create(field: string | null, name: string): void {
        const key = name.trim().toLocaleLowerCase();
        this.update(field, tags => !key || tags.some(t => t.name.toLocaleLowerCase() === key) ? tags : [...tags, { id: null, name: name.trim(), number: null }]);
    }

    protected remove(field: string | null, index: number): void {
        this.update(field, tags => tags.filter((_, i) => i !== index));
    }

    protected move(field: string | null, index: number, step: number): void {
        this.update(field, tags => {
            const to = index + step;
            if (to < 0 || to >= tags.length)
                return tags;
            [tags[index], tags[to]] = [tags[to], tags[index]];
            return tags;
        });
    }

    protected number(field: string | null, index: number, value: string): void {
        const parsed = value.trim() === '' ? null : Number(value);
        this.update(field, tags => tags.map((tag, i) => i === index ? { ...tag, number: parsed !== null && Number.isFinite(parsed) ? parsed : null } : tag));
    }

    protected addField(event: Event): void {
        const key = valueOf(event);
        (event.target as HTMLSelectElement).value = '';
        if (!key)
            return;
        this.draftState.update(draft => {
            if (!draft || draft.fields.some(f => f.field === key))
                return draft;
            const fields = [...draft.fields];
            fields.splice(fields.length - 1, 0, { field: key, tags: [] });
            return { ...draft, fields };
        });
    }

    /**
     * Reordering by drag: the handle takes the pointer, and the value moves past a neighbour once the pointer passes
     * that neighbour's middle (touch and mouse alike; Move up / down for the keyboard).
     */
    protected dragStart(event: PointerEvent, field: string | null, index: number): void {
        const handle = event.currentTarget as HTMLElement;
        const list = handle.closest('ol');
        if (!list)
            return;
        event.preventDefault();
        handle.setPointerCapture?.(event.pointerId);
        let at = index;
        this.dragging.set(`${field}:${at}`);
        const moveTo = (y: number) => {
            const rows = [...list.children] as HTMLElement[];
            const above = rows[at - 1]?.getBoundingClientRect();
            const below = rows[at + 1]?.getBoundingClientRect();
            if (above && y < above.top + above.height / 2) {
                this.move(field, at, -1);
                at--;
            } else if (below && y > below.top + below.height / 2) {
                this.move(field, at, 1);
                at++;
            } else {
                return;
            }
            this.dragging.set(`${field}:${at}`);
        };
        const onMove = (e: PointerEvent) => moveTo(e.clientY);
        const onEnd = () => {
            this.dragging.set(null);
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onEnd);
            handle.removeEventListener('pointercancel', onEnd);
        };
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onEnd);
        handle.addEventListener('pointercancel', onEnd);
    }

    /** Only what changed: a field nobody touched keeps its links as the import made them. */
    private edit(draft: BookDraft): BookEdit {
        const original = this.original!;
        const book = this.book();
        const edit: BookEdit = {};
        if (book.canEdit && (draft.title.trim() !== original.title || draft.description.trim() !== original.description.trim() || draft.rating !== original.rating))
            edit.columns = { title: draft.title.trim() || null, description: draft.description.trim() || null, rating: draft.rating };
        const same = (a: DraftTag[], b: DraftTag[]) => a.length === b.length && a.every((t, i) => t.id === b[i].id && t.name === b[i].name && t.number === b[i].number);
        const fields = this.shownFields().filter(group => !same(group.tags, original.fields.find(f => f.field === group.field)?.tags ?? []));
        if (fields.length)
            edit.fields = fields.map(group => ({
                field: group.field,
                tags: group.tags.map((tag): BookTagEdit => tag.id !== null ? { id: tag.id, number: tag.number } : { name: tag.name, number: tag.number }),
            }));
        return edit;
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft)
            return;
        const edit = this.edit(draft);
        if (!edit.columns && !edit.fields) {
            this.sheet()?.close();
            return;
        }
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            const book = await this.api.editBook(this.book().id, edit);
            this.saved.emit(book);
            this.sheet()?.close();
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
