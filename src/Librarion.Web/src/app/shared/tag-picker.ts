import { ChangeDetectionStrategy, Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { PickedTag } from '../core/models';
import { Icon } from './icon';

/** A suggestion: a tag, or one to create with the typed name. */
export type TagOption = { tag: PickedTag; create?: undefined } | { create: string; tag?: undefined };

// A press elsewhere blurs the box before its click: the list waits for the release (SFE's picker).
let pointerPressed = false;
if (typeof addEventListener === 'function') {
    addEventListener('pointerdown', () => { pointerPressed = true; }, true);
    addEventListener('pointerup', () => { pointerPressed = false; }, true);
    addEventListener('pointercancel', () => { pointerPressed = false; }, true);
}

/**
 * The tag picker, the same everywhere a tag is chosen (stage 6, chunk 5; a port of SFE's, agents/docs/features/tags.md):
 * typing lists the tags of `field` (null: plain tags) whose name or other name starts so, whose shorthand it is, or
 * whose name has its words (`api/tags/pick`), each with its color, the other name it was found by, the parent telling
 * it apart and its books; with `allowCreate`, "Create “name”" when no tag has that name. Arrows and Enter, or a tap,
 * choose; Escape closes the list (not the dialog around it). `exclude` leaves tags out (those already chosen).
 */
@Component({
    selector: 'lib-tag-picker',
    imports: [TranslatePipe, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'tag-picker' },
    template: `
        <input #box type="search" role="combobox" autocomplete="off" spellcheck="false" enterkeyhint="done" aria-autocomplete="list"
               [id]="inputId()" [attr.aria-label]="label()" [placeholder]="(placeholder() || 'picker.placeholder') | t"
               [attr.aria-expanded]="options().length > 0" [attr.aria-controls]="inputId() + '-list'"
               [attr.aria-activedescendant]="active() >= 0 ? inputId() + '-' + active() : null"
               (input)="typed()" (focus)="focused()" (blur)="blurred()" (keydown)="key($event)">
        <ul class="tag-picker-list" role="listbox" [id]="inputId() + '-list'" [attr.aria-label]="label()" [hidden]="!options().length">
            @for (option of options(); track $index; let i = $index) {
                <li role="option" [id]="inputId() + '-' + i" [class.create]="option.create !== undefined" [attr.aria-selected]="i === active()"
                    (mousedown)="$event.preventDefault()" (click)="choose(option)">
                    @if (option.tag; as tag) {
                        <span class="tag-dot" [style.background]="tag.color"></span>
                        <span class="tag-picker-name">
                            {{ tag.name }}
                            @if (tag.parent) {
                                <span class="tag-picker-parent">({{ tag.parent }})</span>
                            }
                            @if (tag.alias) {
                                <span class="tag-picker-detail">{{ 'picker.alias' | t: { name: tag.alias } }}</span>
                            }
                        </span>
                        @if (tag.shorthand) {
                            <span class="tag-picker-shorthand">{{ tag.shorthand }}</span>
                        }
                        <span class="tag-picker-books">{{ 'picker.books' | t: { count: i18n.number(tag.books) } }}</span>
                    } @else {
                        <svg libIcon="plus"></svg><span>{{ 'picker.create' | t: { name: option.create ?? '' } }}</span>
                    }
                </li>
            }
        </ul>
        @if (failed()) {
            <p class="field-error" role="status">{{ 'picker.failed' | t }}</p>
        }
    `,
})
export class TagPicker {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

    readonly field = input<string | null>(null);
    readonly exclude = input<readonly number[]>([]);
    readonly label = input.required<string>();
    readonly placeholder = input('');
    readonly inputId = input('tag-picker');
    readonly allowCreate = input(false);
    readonly picked = output<PickedTag>();
    /** "Create" chosen, with the typed name: the host makes the tag. */
    readonly create = output<string>();

    protected readonly options = signal<TagOption[]>([]);
    protected readonly active = signal(-1);
    protected readonly failed = signal(false);
    private timer: ReturnType<typeof setTimeout> | null = null;
    private asked = 0;

    focus(): void {
        this.box().nativeElement.focus();
    }

    protected typed(): void {
        if (this.timer)
            clearTimeout(this.timer);
        this.timer = setTimeout(() => void this.find(), 150);
    }

    protected focused(): void {
        if (this.box().nativeElement.value.trim())
            void this.find();
    }

    protected blurred(): void {
        // Collapsing the list at once would move what is under the pointer before the release: the click would miss.
        if (pointerPressed)
            addEventListener('pointerup', () => setTimeout(() => {
                if (document.activeElement !== this.box().nativeElement)
                    this.close();
            }), { once: true, capture: true });
        else
            this.close();
    }

    protected key(event: KeyboardEvent): void {
        const options = this.options();
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (!options.length) {
                void this.find(true);
                return;
            }
            const step = event.key === 'ArrowDown' ? 1 : -1;
            this.setActive((this.active() + step + options.length) % options.length);
        } else if (event.key === 'Enter') {
            if (this.active() >= 0) {
                event.preventDefault();
                this.choose(options[this.active()]);
            }
        } else if (event.key === 'Escape' && options.length) {
            event.preventDefault();
            event.stopPropagation();
            this.close();
        }
    }

    protected choose(option: TagOption): void {
        if (option.tag)
            this.picked.emit(option.tag);
        else
            this.create.emit(option.create);
        this.box().nativeElement.value = '';
        this.close();
    }

    private close(): void {
        this.asked++;
        if (this.timer)
            clearTimeout(this.timer);
        this.options.set([]);
        this.active.set(-1);
    }

    /** `browse`: arrows in an empty box list the field's first tags. */
    private async find(browse = false): Promise<void> {
        const text = this.box().nativeElement.value.trim();
        if (!text && !browse) {
            this.close();
            return;
        }
        const asked = ++this.asked;
        try {
            const found = await this.api.pickTags(this.field(), text, 10);
            // An answer overtaken by a later question, or come after the box was left, is dropped.
            if (asked !== this.asked)
                return;
            this.failed.set(false);
            const exclude = this.exclude();
            const options: TagOption[] = found.items.filter(tag => !exclude.includes(tag.id)).slice(0, 8).map(tag => ({ tag }));
            if (this.allowCreate() && text && !found.exact)
                options.push({ create: text });
            this.options.set(options);
            this.setActive(options.length ? 0 : -1);
        } catch {
            if (asked !== this.asked)
                return;
            this.options.set([]);
            this.failed.set(true);
        }
    }

    private setActive(index: number): void {
        this.active.set(index);
        const id = `${this.inputId()}-${index}`;
        queueMicrotask(() => document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' }));
    }
}
