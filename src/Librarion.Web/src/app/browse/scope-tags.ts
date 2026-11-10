import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookScope, PickedTag } from '../core/models';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';

/** A tag chosen to give (`id` null: made by name on Apply) or to take away. */
interface Chosen {
    id: number | null;
    name: string;
    color: string | null;
}

/**
 * Tagging every book of a list (stage 6, chunk 7; SFE's scope tagging, agents/docs/features/tags.md): the list's books
 * counted first (`GET api/books/scope`, never listed), the tags to give and to take away chosen with the shared
 * picker, then one confirmed Apply to that count. A big change runs in the background: the progress panel follows it.
 */
@Component({
    selector: 'lib-scope-tags',
    imports: [TranslatePipe, Sheet, TagPicker, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'tagAll.heading' | t" [subtitle]="subtitle()" (closed)="closed.emit()">
            @if (count() === null && !error()) {
                <p class="field-hint" role="status">{{ 'tagAll.counting' | t }}</p>
            } @else {
                <form id="scope-tags-form" class="selection-tags" novalidate (submit)="apply($event)">
                    @if (count() !== null) {
                        <p class="scope-count">{{ (capped() ? 'tagAll.count.atLeast' : count() === 1 ? 'tagAll.count.one' : 'tagAll.count') | t: { count: i18n.number(count()!) } }}</p>
                    }
                    <fieldset class="field">
                        <legend class="field-label">{{ 'tagAll.add' | t }}</legend>
                        @if (adding().length) {
                            <ul class="tag-chips">
                                @for (tag of adding(); track tag.id ?? tag.name; let i = $index) {
                                    <li class="chip">
                                        <span class="tag-dot" [style.background]="tag.color"></span><span>{{ tag.name }}</span>
                                        <button type="button" class="icon-button" [attr.aria-label]="'tagAll.removeChip' | t: { name: tag.name }"
                                                [title]="'tagAll.removeChip' | t: { name: tag.name }" (click)="drop('add', i)">
                                            <svg libIcon="close"></svg>
                                        </button>
                                    </li>
                                }
                            </ul>
                        }
                        <lib-tag-picker inputId="scope-tag-add" [label]="'tagAll.addTag' | t" [allowCreate]="true" [exclude]="chosenIds()"
                                        (picked)="pick('add', $event)" (create)="create($event)" />
                    </fieldset>
                    <fieldset class="field">
                        <legend class="field-label">{{ 'tagAll.remove' | t }}</legend>
                        @if (removing().length) {
                            <ul class="tag-chips">
                                @for (tag of removing(); track tag.id; let i = $index) {
                                    <li class="chip">
                                        <span class="tag-dot" [style.background]="tag.color"></span><span>{{ tag.name }}</span>
                                        <button type="button" class="icon-button" [attr.aria-label]="'tagAll.removeChip' | t: { name: tag.name }"
                                                [title]="'tagAll.removeChip' | t: { name: tag.name }" (click)="drop('remove', i)">
                                            <svg libIcon="close"></svg>
                                        </button>
                                    </li>
                                }
                            </ul>
                        }
                        <lib-tag-picker inputId="scope-tag-remove" [label]="'tagAll.removeTag' | t" [exclude]="chosenIds()" (picked)="pick('remove', $event)" />
                    </fieldset>
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                </form>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="submit" form="scope-tags-form" class="button primary" [disabled]="busy() || !ready()">
                    {{ (capped() ? 'tagAll.confirm.atLeast' : count() === 1 ? 'tagAll.confirm.one' : 'tagAll.confirm') | t: { count: i18n.number(count() ?? 0) } }}
                </button>
            </div>
        </lib-sheet>
    `,
})
export class ScopeTags implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);
    private readonly tasks = inject(TaskWatch);

    readonly scope = input.required<BookScope>();
    /** What the list is, under the heading ("Books with Fantasy", a search). */
    readonly subtitle = input<string | null>(null);
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly count = signal<number | null>(null);
    /** Counted up to the server's cap: at least `count` books. */
    protected readonly capped = signal(false);
    protected readonly adding = signal<Chosen[]>([]);
    protected readonly removing = signal<Chosen[]>([]);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly chosenIds = computed(() => [...this.adding(), ...this.removing()].flatMap(tag => tag.id === null ? [] : [tag.id]));
    protected readonly ready = computed(() => !!this.count() && this.adding().length + this.removing().length > 0);

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        try {
            const counted = await this.api.countScope(this.scope());
            this.capped.set(counted.capped);
            this.count.set(counted.books);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected pick(list: 'add' | 'remove', tag: PickedTag): void {
        const target = list === 'add' ? this.adding : this.removing;
        target.update(tags => tags.some(t => t.id === tag.id) ? tags : [...tags, { id: tag.id, name: tag.name, color: tag.color }]);
    }

    protected create(name: string): void {
        const key = name.trim().toLocaleLowerCase();
        this.adding.update(tags => !key || tags.some(t => t.name.toLocaleLowerCase() === key) ? tags : [...tags, { id: null, name: name.trim(), color: null }]);
    }

    protected drop(list: 'add' | 'remove', index: number): void {
        (list === 'add' ? this.adding : this.removing).update(tags => tags.filter((_, i) => i !== index));
    }

    protected async apply(event: Event): Promise<void> {
        event.preventDefault();
        if (!this.ready())
            return;
        this.busy.set(true);
        this.error.set(null);
        try {
            const work = await this.api.tagScope(this.scope(), {
                add: this.adding().map(tag => tag.id !== null ? { id: tag.id } : { name: tag.name }),
                remove: this.removing().map(tag => tag.id!),
            });
            if (work.runId !== null) {
                this.tasks.follow(work.runId);
                this.toasts.show(this.i18n.t(this.capped() ? 'tagAll.running.atLeast' : 'tagAll.running', { count: this.i18n.number(this.count()!) }));
            } else {
                this.toasts.show(this.i18n.t('tagAll.done', { count: this.i18n.number(work.links) }));
            }
            this.sheet()?.close();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
