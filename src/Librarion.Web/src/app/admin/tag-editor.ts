import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { checkedOf, errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ManagedField, ManagedTag, TagDefinition, TagInput } from '../core/models';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { TagSearch } from './tag-search';

interface TagDraft {
    name: string;
    sortName: string;
    shorthand: string;
    color: string | null;
    isCategory: boolean;
    isHidden: boolean;
    aliases: string;
    parents: { id: number; name: string }[];
    disambiguationParentId: number | null;
    fieldKey: string;
}

/** What the editor is opened on: a tag (`id`), a new tag of `field` (null: plain), or a new field (`newField`). */
export interface TagEditorTarget {
    id: number | null;
    field: ManagedField | null;
    newField?: boolean;
}

/**
 * A tag's editor (port of SFE's tag-editor dialog; agents/docs/features/tags.md): name, sort name, shorthand, color,
 * category and hidden, other names one per line, parents found by name (of the same kind: plain tags, or values of
 * the same field) and which of them tells it apart. A field's own row has no parents; a built-in field keeps its name.
 * Delete and Merge are the section's (the editor asks for them).
 */
@Component({
    selector: 'lib-tag-editor',
    imports: [TranslatePipe, Sheet, Icon, TagSearch],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="heading()" [subtitle]="subtitle()" (closed)="closed.emit()">
            @if (draft(); as draft) {
                <form id="tag-form" class="tag-editor" novalidate (submit)="save($event)">
                    @if (target().newField) {
                        <div class="field">
                            <label class="field-label" for="tag-key">{{ 'tagAdmin.fieldKey' | t }}</label>
                            <input id="tag-key" type="text" autocomplete="off" spellcheck="false" [value]="draft.fieldKey" (input)="patch({ fieldKey: valueOf($event) })">
                            <span class="field-hint">{{ 'tagAdmin.fieldKeyHint' | t }}</span>
                            @if (errors()['fieldKey']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                    }
                    <div class="field">
                        <label class="field-label" for="tag-name">{{ 'tagAdmin.name' | t }}</label>
                        <input id="tag-name" type="text" autocomplete="off" [disabled]="keepsName()" [value]="draft.name" (input)="patch({ name: valueOf($event) })">
                        @if (errors()['name']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </div>
                    @if (!isField()) {
                        <div class="field">
                            <label class="field-label" for="tag-sort">{{ 'tagAdmin.sortName' | t }}</label>
                            <input id="tag-sort" type="text" autocomplete="off" [placeholder]="'tagAdmin.sortNameHint' | t" [value]="draft.sortName" (input)="patch({ sortName: valueOf($event) })">
                        </div>
                    }
                    <div class="tag-editor-pair">
                        <div class="field">
                            <label class="field-label" for="tag-shorthand">{{ 'tagAdmin.shorthand' | t }}</label>
                            <input id="tag-shorthand" type="text" autocomplete="off" [value]="draft.shorthand" (input)="patch({ shorthand: valueOf($event) })">
                            @if (errors()['shorthand']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                        <div class="field">
                            <span class="field-label" id="tag-color-label">{{ 'tagAdmin.color' | t }}</span>
                            <div class="tag-color">
                                <input type="color" aria-labelledby="tag-color-label" [value]="draft.color ?? '#808080'" (input)="patch({ color: valueOf($event) })">
                                <label class="check">
                                    <input type="checkbox" [checked]="draft.color === null" (change)="patch({ color: checkedOf($event) ? null : '#808080' })">
                                    <span>{{ 'tagAdmin.noColor' | t }}</span>
                                </label>
                            </div>
                            @if (errors()['color']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </div>
                    </div>
                    <div class="checks">
                        @if (!isField()) {
                            <label class="check">
                                <input type="checkbox" [checked]="draft.isCategory" (change)="patch({ isCategory: checkedOf($event) })">
                                <span>{{ 'tagAdmin.category' | t }}<span class="field-hint">{{ 'tagAdmin.categoryHint' | t }}</span></span>
                            </label>
                        }
                        <label class="check">
                            <input type="checkbox" [checked]="draft.isHidden" (change)="patch({ isHidden: checkedOf($event) })">
                            <span>{{ 'tagAdmin.hidden' | t }}<span class="field-hint">{{ 'tagAdmin.hiddenHint' | t }}</span></span>
                        </label>
                    </div>
                    <div class="field">
                        <label class="field-label" for="tag-aliases">{{ 'tagAdmin.aliases' | t }}</label>
                        <textarea id="tag-aliases" rows="3" [value]="draft.aliases" (input)="patch({ aliases: valueOf($event) })"></textarea>
                        <span class="field-hint">{{ 'tagAdmin.aliasesHint' | t }}</span>
                        @if (errors()['aliases']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </div>
                    @if (!isField()) {
                        <fieldset class="field">
                            <legend class="field-label">{{ 'tagAdmin.parents' | t }}</legend>
                            @if (draft.parents.length) {
                                <ul class="tag-chips">
                                    @for (parent of draft.parents; track parent.id) {
                                        <li class="chip">
                                            <span>{{ parent.name }}</span>
                                            <button type="button" class="icon-button" [attr.aria-label]="'tagAdmin.removeParent' | t: { name: parent.name }"
                                                    [title]="'tagAdmin.removeParent' | t: { name: parent.name }" (click)="removeParent(parent.id)">
                                                <svg libIcon="close"></svg>
                                            </button>
                                        </li>
                                    }
                                </ul>
                            }
                            <lib-tag-search inputId="tag-parent" [label]="'tagAdmin.addParent' | t" [field]="target().field?.key ?? null"
                                            [exclude]="excluded()" (picked)="addParent($event)" />
                            <span class="field-hint">{{ 'tagAdmin.parentsHint' | t }}</span>
                            @if (errors()['parentIds']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                        </fieldset>
                        @if (draft.parents.length) {
                            <div class="field">
                                <label class="field-label" for="tag-disambiguation">{{ 'tagAdmin.disambiguation' | t }}</label>
                                <select id="tag-disambiguation" (change)="patch({ disambiguationParentId: numberOf(valueOf($event)) })">
                                    <option value="" [selected]="draft.disambiguationParentId === null">{{ 'tagAdmin.noDisambiguation' | t }}</option>
                                    @for (parent of draft.parents; track parent.id) {
                                        <option [value]="parent.id" [selected]="draft.disambiguationParentId === parent.id">{{ draft.name }} ({{ parent.name }})</option>
                                    }
                                </select>
                                <span class="field-hint">{{ 'tagAdmin.disambiguationHint' | t }}</span>
                                @if (errors()['disambiguationParentId']; as error) {
                                    <span class="field-error">{{ error }}</span>
                                }
                            </div>
                        }
                    }
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                </form>
            } @else {
                <div class="skeleton-form" aria-busy="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line"></span></div>
            }
            <div sheetFooter class="sheet-actions">
                @if (definition(); as tag) {
                    @if (!tag.builtIn) {
                        <button type="button" class="button danger" [title]="'tagAdmin.deleteHint' | t" [disabled]="busy()" (click)="remove.emit(tag)">{{ 'common.delete' | t }}</button>
                        <button type="button" class="button" [title]="'tagAdmin.mergeHint' | t" [disabled]="busy()" (click)="merge.emit(tag)">{{ 'tagAdmin.merge' | t }}</button>
                    }
                }
                <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="submit" form="tag-form" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy() || !draft()">{{ 'common.save' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class TagEditor implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);

    readonly target = input.required<TagEditorTarget>();
    readonly saved = output<TagDefinition>();
    readonly closed = output<void>();
    readonly remove = output<TagDefinition>();
    readonly merge = output<TagDefinition>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly definition = signal<TagDefinition | null>(null);
    protected readonly draftState = signal<TagDraft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});

    protected readonly isField = computed(() => !!this.target().newField || !!this.definition()?.fieldKey);
    protected readonly keepsName = computed(() => !!this.definition()?.fieldKey && !!this.definition()?.builtIn);
    protected readonly excluded = computed(() => [...(this.definition() ? [this.definition()!.id] : []), ...(this.draft()?.parents.map(p => p.id) ?? [])]);
    protected readonly heading = computed(() => {
        const target = this.target();
        if (target.newField)
            return this.i18n.t('tagAdmin.newFieldTitle');
        if (target.id === null)
            return this.i18n.t(target.field ? 'tagAdmin.newValueTitle' : 'tagAdmin.newTitle');
        return this.definition()?.name ?? this.i18n.t('tagAdmin.editTitle');
    });
    protected readonly subtitle = computed(() => {
        const tag = this.definition();
        return tag ? this.i18n.t('tagAdmin.books', { count: tag.books }) : null;
    });

    protected readonly valueOf = valueOf;
    protected readonly checkedOf = checkedOf;

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        const target = this.target();
        if (target.id === null) {
            this.draftState.set({
                name: '', sortName: '', shorthand: '', color: null, isCategory: !!target.newField, isHidden: false, aliases: '', parents: [],
                disambiguationParentId: null, fieldKey: '',
            });
            return;
        }
        try {
            const tag = await this.api.tagDefinition(target.id);
            this.definition.set(tag);
            this.draftState.set({
                name: tag.fieldKey && tag.builtIn ? this.i18n.t(`fields.name.${tag.fieldKey}`) : tag.name,
                sortName: tag.sortName === tag.name ? '' : tag.sortName, shorthand: tag.shorthand ?? '', color: tag.color,
                isCategory: tag.isCategory, isHidden: tag.isHidden, aliases: tag.aliases.join('\n'), parents: tag.parents,
                disambiguationParentId: tag.disambiguationParentId, fieldKey: tag.fieldKey ?? '',
            });
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected patch(change: Partial<TagDraft>): void {
        this.draftState.update(draft => draft && { ...draft, ...change });
    }

    protected addParent(tag: ManagedTag): void {
        this.draftState.update(draft => draft && { ...draft, parents: [...draft.parents, { id: tag.id, name: tag.name }] });
    }

    protected removeParent(id: number): void {
        this.draftState.update(draft => draft && {
            ...draft, parents: draft.parents.filter(p => p.id !== id),
            disambiguationParentId: draft.disambiguationParentId === id ? null : draft.disambiguationParentId,
        });
    }

    protected numberOf(value: string): number | null {
        return value ? Number(value) : null;
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft)
            return;
        const target = this.target();
        const input: TagInput = {
            name: draft.name.trim(), sortName: draft.sortName.trim() || null, shorthand: draft.shorthand.trim() || null, color: draft.color,
            isCategory: draft.isCategory, isHidden: draft.isHidden, aliases: draft.aliases.split('\n').map(a => a.trim()).filter(Boolean),
            parentIds: draft.parents.map(p => p.id), disambiguationParentId: draft.disambiguationParentId,
        };
        this.busy.set(true);
        this.error.set(null);
        this.errors.set({});
        try {
            const id = this.definition()?.id;
            const tag = id !== undefined
                ? await this.api.updateTag(id, input)
                : await this.api.createTag({ ...input, fieldKey: target.newField ? draft.fieldKey.trim() : null, fieldId: target.newField ? null : target.field?.id ?? null });
            this.saved.emit(tag);
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
