import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { valueOf } from '../core/errors';
import { TranslatePipe } from '../core/i18n.service';
import { ManagedTag } from '../core/models';

/**
 * Finds a tag of one field (`field` null: plain tags) by what is typed: its name's start or its words, the first 8
 * (`api/tags/manage?q`), `exclude` left out; arrows and Enter, or a tap, pick one. The tag manager's own small chooser
 * (parents, a merge's target); the shared tag picker comes in stage 6, chunk 5.
 */
@Component({
    selector: 'lib-tag-search',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="tag-search">
            <input type="search" role="combobox" autocomplete="off" [id]="inputId()" [attr.aria-label]="label()" [placeholder]="'tagAdmin.findPlaceholder' | t"
                   [attr.aria-expanded]="found().length > 0" [attr.aria-controls]="inputId() + '-list'"
                   [attr.aria-activedescendant]="active() >= 0 ? inputId() + '-' + active() : null"
                   [value]="text()" (input)="typed(valueOf($event))" (keydown)="key($event)">
            @if (found().length) {
                <ul class="tag-search-list" role="listbox" [id]="inputId() + '-list'">
                    @for (tag of found(); track tag.id; let i = $index) {
                        <li role="option" [id]="inputId() + '-' + i" [attr.aria-selected]="i === active()" (click)="pick(tag)">
                            <span class="tag-dot" [style.background]="tag.color"></span>
                            <span>{{ tag.name }}</span>
                            <span class="field-hint">{{ 'tagAdmin.books' | t: { count: tag.books } }}</span>
                        </li>
                    }
                </ul>
            }
        </div>
    `,
})
export class TagSearch {
    private readonly api = inject(Api);

    readonly field = input<string | null>(null);
    readonly exclude = input<number[]>([]);
    readonly label = input.required<string>();
    readonly inputId = input('tag-search');
    readonly picked = output<ManagedTag>();

    protected readonly text = signal('');
    protected readonly found = signal<ManagedTag[]>([]);
    protected readonly active = signal(-1);
    private timer: ReturnType<typeof setTimeout> | null = null;
    private asked = 0;

    protected readonly valueOf = valueOf;

    protected typed(text: string): void {
        this.text.set(text);
        if (this.timer)
            clearTimeout(this.timer);
        if (!text.trim()) {
            this.found.set([]);
            return;
        }
        this.timer = setTimeout(() => void this.find(text), 200);
    }

    protected key(event: KeyboardEvent): void {
        const count = this.found().length;
        if (event.key === 'ArrowDown' && count) {
            event.preventDefault();
            this.active.update(i => (i + 1) % count);
        } else if (event.key === 'ArrowUp' && count) {
            event.preventDefault();
            this.active.update(i => (i <= 0 ? count : i) - 1);
        } else if (event.key === 'Enter' && this.active() >= 0) {
            event.preventDefault();
            this.pick(this.found()[this.active()]);
        } else if (event.key === 'Escape' && count) {
            // Closes the list, not the dialog around it.
            event.stopPropagation();
            event.preventDefault();
            this.found.set([]);
        }
    }

    protected pick(tag: ManagedTag): void {
        this.picked.emit(tag);
        this.text.set('');
        this.found.set([]);
        this.active.set(-1);
    }

    private async find(text: string): Promise<void> {
        const asked = ++this.asked;
        try {
            const page = await this.api.managedTags(this.field(), { q: text, limit: 10 });
            // An answer overtaken by a later question is dropped.
            if (asked !== this.asked)
                return;
            const exclude = this.exclude();
            this.found.set(page.items.filter(tag => !exclude.includes(tag.id)).slice(0, 8));
            this.active.set(this.found().length ? 0 : -1);
        } catch {
            if (asked === this.asked)
                this.found.set([]);
        }
    }
}
