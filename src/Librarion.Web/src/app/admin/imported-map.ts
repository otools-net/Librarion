import { ChangeDetectionStrategy, Component, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ImportedField, ImportedTag, TagWork } from '../core/models';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';

/**
 * Mapping imported tags onto one tag of their field (agents/docs/features/imported-tags.md): the target picked (or made),
 * then what will happen said in numbers before the button: each tag merged into it, its raw names giving it from now on,
 * each merge undone under Tags › Merges.
 */
@Component({
    selector: 'lib-imported-map',
    imports: [TranslatePipe, Sheet, TagPicker],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'importedTags.mapTitle' | t: { count: i18n.number(tags().length) }" (closed)="closed.emit()">
            @if (target(); as chosen) {
                <p class="confirm-message">{{ 'importedTags.mapInto' | t: { count: i18n.number(tags().length), books: i18n.number(books()), target: chosen.name } }}</p>
                <button type="button" class="button" (click)="target.set(null)">{{ 'tagAdmin.mergeOther' | t }}</button>
            } @else {
                <lib-tag-picker inputId="imported-map-target" [label]="'importedTags.mapTarget' | t" [field]="field().key" [allowCreate]="true"
                                [exclude]="ids()" (picked)="target.set($event)" (create)="create($event)" />
                <span class="field-hint">{{ 'importedTags.mapTargetHint' | t }}</span>
            }
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" autofocus (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="button" class="button primary" [disabled]="busy() || !target()" (click)="go()">{{ 'importedTags.map' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class ImportedMap {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);

    readonly tags = input.required<ImportedTag[]>();
    readonly field = input.required<ImportedField>();
    readonly done = output<TagWork>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly target = signal<{ id: number; name: string } | null>(null);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected ids(): number[] {
        return this.tags().map(t => t.id);
    }

    protected books(): number {
        return this.tags().reduce((sum, t) => sum + t.books, 0);
    }

    protected async create(name: string): Promise<void> {
        this.error.set(null);
        try {
            const tag = await this.api.createTag({ name, fieldId: this.field().id });
            this.target.set({ id: tag.id, name: tag.name });
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected async go(): Promise<void> {
        const target = this.target();
        if (!target)
            return;
        this.busy.set(true);
        this.error.set(null);
        try {
            this.done.emit(await this.api.mapImported(this.ids(), target.id));
            this.sheet()?.close();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
