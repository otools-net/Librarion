import { ChangeDetectionStrategy, Component, OnInit, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ShelfInfo } from '../core/models';
import { ShelfStore } from '../core/shelf-store';
import { ToastService } from '../core/toast.service';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';

/**
 * Add to shelf (agents/docs/features/shelves.md): the account's shelves with a box each, for a book (ticked: the shelves
 * holding it) or a selection (none ticked to begin with). A box puts the books on that shelf or takes them off at
 * once; New shelf makes one and puts them on it. `changed` names each shelf changed (its page shows it again).
 */
@Component({
    selector: 'lib-shelf-picker',
    imports: [TranslatePipe, Sheet, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="(books().length === 1 ? 'shelfPick.heading.one' : 'shelfPick.heading') | t: { count: i18n.number(books().length) }"
                   [subtitle]="subtitle()" (closed)="closed.emit()">
            @if (ready()) {
                @if (store.shelves().length) {
                    <ul class="shelf-pick-rows" [attr.aria-label]="'shelfPick.list' | t">
                        @for (shelf of store.shelves(); track shelf.id) {
                            <li>
                                <label class="check">
                                    <input type="checkbox" [checked]="on().has(shelf.id)" [disabled]="busy()" (change)="toggle(shelf, $event)">
                                    <svg libIcon="shelf"></svg>
                                    <span class="shelf-pick-name">{{ shelf.name }}</span>
                                    <span class="field-hint">{{ i18n.number(shelf.bookCount) }}</span>
                                </label>
                            </li>
                        }
                    </ul>
                } @else {
                    <p class="field-hint">{{ 'shelfPick.none' | t }}</p>
                }
                <form class="shelf-new" novalidate (submit)="create($event)">
                    <label class="field">
                        <span class="field-label">{{ 'shelfPick.new' | t }}</span>
                        <input type="text" name="shelf-name" maxlength="100" autocomplete="off" [value]="name()" (input)="name.set(valueOf($event))">
                    </label>
                    <button type="submit" class="button" [title]="'shelfPick.createHint' | t" [disabled]="busy() || !name().trim()">
                        <svg libIcon="plus"></svg>{{ 'shelfPick.create' | t }}
                    </button>
                </form>
            } @else if (!error()) {
                <p class="field-hint" role="status">{{ 'common.loading' | t }}</p>
            }
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button primary" [title]="'common.closeHint' | t" (click)="sheet()?.close()">{{ 'shelfPick.done' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class ShelfPicker implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly store = inject(ShelfStore);
    private readonly toasts = inject(ToastService);

    readonly books = input.required<number[]>();
    /** Under the heading: the book's title, for one. */
    readonly subtitle = input<string | null>(null);
    readonly changed = output<number>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly on = signal<ReadonlySet<number>>(new Set());
    protected readonly ready = signal(false);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly name = signal('');
    protected readonly valueOf = valueOf;

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        try {
            const [, holding] = await Promise.all([
                this.store.load(),
                this.books().length === 1 ? this.api.bookShelves(this.books()[0]) : Promise.resolve([] as number[]),
            ]);
            this.on.set(new Set(holding));
            this.ready.set(true);
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected async toggle(shelf: ShelfInfo, event: Event): Promise<void> {
        const put = (event.target as HTMLInputElement).checked;
        await this.run(async () => {
            if (put)
                await this.put(shelf.id, shelf.name);
            else {
                this.store.put(await this.api.removeFromShelf(shelf.id, this.books()));
                this.mark(shelf.id, false);
                this.toasts.show(this.i18n.t('shelfPick.removed', { name: shelf.name, count: this.i18n.number(this.books().length) }));
            }
            this.changed.emit(shelf.id);
        }, event.target as HTMLInputElement, !put);
    }

    protected async create(event: Event): Promise<void> {
        event.preventDefault();
        const name = this.name().trim();
        if (!name)
            return;
        await this.run(async () => {
            const shelf = await this.api.createShelf(name);
            this.store.put(shelf);
            this.name.set('');
            await this.put(shelf.id, shelf.name);
            this.changed.emit(shelf.id);
        });
    }

    private async put(id: number, name: string): Promise<void> {
        const result = await this.api.addToShelf(id, this.books());
        this.store.put(result.shelf);
        this.mark(id, true);
        this.toasts.show(result.added
            ? this.i18n.t(result.already ? 'shelfPick.addedSome' : 'shelfPick.added', {
                name, count: this.i18n.number(result.added), already: this.i18n.number(result.already),
            })
            : this.i18n.t('shelfPick.already', { name }));
    }

    private mark(id: number, on: boolean): void {
        this.on.update(set => {
            const next = new Set(set);
            if (on)
                next.add(id);
            else
                next.delete(id);
            return next;
        });
    }

    /** Runs a change; on a refusal the box goes back to how it was. */
    private async run(change: () => Promise<void>, box?: HTMLInputElement, was?: boolean): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await change();
        } catch (error) {
            if (box && was !== undefined)
                box.checked = was;
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
