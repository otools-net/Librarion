import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ManagedField, ManagedTag, PickedTag, TagWork } from '../core/models';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';

/** What is merged: a tag of `field` (null: a plain tag), or the field itself (`tag` null). */
export interface MergeSource {
    tag: { id: number; name: string; books: number } | null;
    field: ManagedField | null;
}

/**
 * Merging a tag into another of the same kind, or a field into another field (agents/docs/features/merging.md): the
 * target chosen, then what will happen said in numbers (how many books change, the name kept as another name, undone
 * under Merges) before the button.
 */
@Component({
    selector: 'lib-tag-merge',
    imports: [TranslatePipe, Sheet, TagPicker],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'tagAdmin.mergeTitle' | t: { name: sourceName() }" (closed)="closed.emit()">
            @if (source().tag) {
                @if (target(); as target) {
                    <p class="confirm-message">{{ 'tagAdmin.mergeInto' | t: { source: sourceName(), target: target.name, count: source().tag!.books } }}</p>
                    <button type="button" class="button" (click)="target$.set(null)">{{ 'tagAdmin.mergeOther' | t }}</button>
                } @else {
                    <lib-tag-picker inputId="merge-target" [label]="'tagAdmin.mergeTarget' | t" [field]="source().field?.key ?? null"
                                    [exclude]="[source().tag!.id]" (picked)="target$.set($event)" />
                    <span class="field-hint">{{ 'tagAdmin.mergeTargetHint' | t }}</span>
                }
            } @else {
                <div class="field">
                    <label class="field-label" for="merge-field">{{ 'tagAdmin.mergeFieldTarget' | t }}</label>
                    <select id="merge-field" (change)="fieldKey.set(valueOf($event))">
                        <option value="" [selected]="!fieldKey()"></option>
                        @for (field of otherFields(); track field.id) {
                            <option [value]="field.key" [selected]="fieldKey() === field.key">{{ fieldName(field) }} ({{ field.key }}:)</option>
                        }
                    </select>
                </div>
                @if (fieldKey()) {
                    <p class="confirm-message">{{ 'tagAdmin.mergeFieldInto' | t: { source: source().field!.key, target: fieldKey() } }}</p>
                }
            }
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" autofocus (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="button" class="button primary" [disabled]="busy() || !ready()" (click)="go()">{{ 'tagAdmin.merge' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class TagMerge {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);

    readonly source = input.required<MergeSource>();
    /** Every field (a field merges into one of the others). */
    readonly fields = input<ManagedField[]>([]);
    readonly done = output<TagWork>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly target$ = signal<PickedTag | null>(null);
    protected readonly target = this.target$.asReadonly();
    protected readonly fieldKey = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly sourceName = computed(() => this.source().tag?.name ?? `${this.source().field?.key}:`);
    protected readonly otherFields = computed(() => this.fields().filter(f => f.id !== this.source().field?.id));
    protected readonly ready = computed(() => this.source().tag ? !!this.target() : !!this.fieldKey());

    protected readonly valueOf = valueOf;

    constructor() {
        effect(() => this.sheet()?.open());
    }

    protected fieldName(field: ManagedField): string {
        return field.builtIn ? this.i18n.t(`fields.name.${field.key}`) : field.name;
    }

    protected async go(): Promise<void> {
        const source = this.source();
        this.busy.set(true);
        this.error.set(null);
        try {
            const work = source.tag
                ? await this.api.mergeTag(source.tag.id, this.target()!.id)
                : await this.api.mergeField(source.field!.key, this.fieldKey());
            this.done.emit(work);
            this.sheet()?.close();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
