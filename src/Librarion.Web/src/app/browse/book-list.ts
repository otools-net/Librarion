import {
    ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, afterRenderEffect, computed, effect, inject, input, signal, untracked,
    viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, coverUrl } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookListItem, BookSort } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ShelfStore } from '../core/shelf-store';
import { ToastService } from '../core/toast.service';
import { Icon } from '../shared/icon';
import { ShelfPicker } from '../shelves/shelf-picker';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { BrowseState, BrowseView, SortScope } from './browse-state';
import { ListWindow } from './list-window';
import { ScopeTags } from './scope-tags';
import { SelectionTags } from './selection-tags';
import { ShelfMoveDialog } from './shelf-move';
import { DropTarget, ShelfDrag, ShelfMove, inListOrder, moveIndex, movedLocally } from './shelf-order';

/** How long a finger rests on a book before it is selected instead of opened (ms). */
const LONG_PRESS = 500;
/** The most books a selection holds (the server's cap). */
const MAX_SELECTION = 1000;

/** Books asked for at a time when the account's `ui.pageSize` says nothing. */
const DEFAULT_PAGE = 50;

/** Keys on a book's grip in a shelf's own order: where they move it. */
const MOVE_KEYS: Record<string, ShelfMove> = { ArrowUp: 'up', ArrowLeft: 'up', ArrowDown: 'down', ArrowRight: 'down', Home: 'start', End: 'end' };

/**
 * Books of a library, all or those with a tag (agents/docs/features/browse.md, D-5.5): a grid of covers or a dense
 * list, the sort and view chosen in its tools and remembered per browser. Pages come by key as the end nears; only the
 * rows in view are drawn (`ListWindow`), so millions stay light. Leaving and coming back shows the list as it was,
 * scrolled where it was (`BrowseState`). The page's heading is projected beside the tools, `[listTools]` before them;
 * `[emptyExtra]` after the empty text. With `library.tag`, books are selected (stage 6, chunk 7; SFE's selection,
 * agents/docs/features/browse.md): the box on a card, Ctrl / Cmd-click, Shift-click for a range, a long press on a
 * phone; while some are, a click selects instead of opening. The bar under the list tags them (`SelectionTags`);
 * Tag all tags every book of the list (`ScopeTags`). Escape clears. With `account.shelves` books are selected too, and
 * the bar puts them on shelves (`ShelfPicker`). With `shelf`, the list is that shelf's books (agents/docs/features/shelves.md):
 * the bar takes them off it, and in the shelf's own order each book has a grip: dragged (`ShelfDrag`), moved by the
 * arrow keys, Home and End, or clicked for Move (`ShelfMoveDialog`); a selected book moves with the selection.
 */
@Component({
    selector: 'lib-book-list',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink, SelectionTags, ScopeTags, ShelfPicker, ShelfMoveDialog],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <header class="browse-head">
            <div class="browse-title"><ng-content /></div>
            <div class="browse-tools">
                <ng-content select="[listTools]" />
                @if (canTag() && items().length && shelf() === null) {
                    <button type="button" class="button" [title]="'tagAll.hint' | t" (click)="dialog.set('scope')">
                        <svg libIcon="tag"></svg><span class="tool-label">{{ 'tagAll.button' | t }}</span>
                    </button>
                }
                <label class="browse-sort">
                    <span class="visually-hidden">{{ 'browse.sort' | t }}</span>
                    <select [title]="'browse.sortHint' | t" (change)="chooseSort($event)">
                        @for (choice of sorts(); track choice) {
                            <option [value]="choice" [selected]="choice === sort()">{{ (sortLabels() + choice) | t }}</option>
                        }
                    </select>
                </label>
                <div class="segmented" role="group" [attr.aria-label]="'browse.view' | t">
                    <button type="button" class="button icon-only" [class.active]="view() === 'grid'" [attr.aria-pressed]="view() === 'grid'"
                            [attr.aria-label]="'browse.view.grid' | t" [title]="'browse.view.gridHint' | t" (click)="chooseView('grid')">
                        <svg libIcon="grid"></svg>
                    </button>
                    <button type="button" class="button icon-only" [class.active]="view() === 'list'" [attr.aria-pressed]="view() === 'list'"
                            [attr.aria-label]="'browse.view.list' | t" [title]="'browse.view.listHint' | t" (click)="chooseView('list')">
                        <svg libIcon="list"></svg>
                    </button>
                </div>
            </div>
        </header>

        @if (items().length) {
            <ul #list class="books" [class.grid]="view() === 'grid'" [class.list]="view() === 'list'" [class.sorting]="!!drag.dragging()" [attr.aria-label]="label()"
                [style.grid-template-columns]="view() === 'grid' ? 'repeat(' + window.columns() + ', minmax(0, 1fr))' : null"
                [style.padding-top.px]="window.range().before" [style.padding-bottom.px]="window.range().after">
                @for (entry of shown(); track entry.book.id) {
                    @let book = entry.book;
                    <li class="book" [class.selected]="selected().has(book.id)" [class.dragging]="drag.dragging()?.has(book.id)"
                        [class.drop-before]="drag.target()?.id === book.id && !drag.target()?.after" [class.drop-after]="drag.target()?.id === book.id && drag.target()?.after"
                        [attr.data-book]="book.id" [attr.aria-posinset]="entry.index + 1" [attr.aria-setsize]="setSize()">
                        <a class="book-link" [routerLink]="['/books', book.id]">
                            <span class="book-cover">
                                <img [src]="cover(book)" alt="" loading="lazy" decoding="async" [width]="view() === 'grid' ? 240 : 80" [height]="view() === 'grid' ? 360 : 120">
                            </span>
                            <span class="book-text">
                                <span class="book-title" [title]="title(book)">{{ title(book) }}</span>
                                <span class="book-authors">{{ book.authors.join(', ') }}</span>
                                @if (book.series) {
                                    <span class="book-series">{{ series(book) }}</span>
                                }
                                @if (view() === 'list' && book.formats.length) {
                                    <span class="book-formats">
                                        @for (format of book.formats; track format) {
                                            <span class="badge">{{ format }}</span>
                                        }
                                    </span>
                                }
                            </span>
                        </a>
                        @if (canSelect()) {
                            <button type="button" class="book-select" role="checkbox" [attr.aria-checked]="selected().has(book.id)"
                                    [attr.aria-label]="'select.toggle' | t: { title: title(book) }" [title]="'select.toggle' | t: { title: title(book) }"
                                    (click)="toggle(book.id)">
                                <svg libIcon="check"></svg>
                            </button>
                        }
                        @if (ordering()) {
                            <button type="button" class="book-move" [attr.aria-label]="'shelfMove.grip' | t: { title: title(book) }" [title]="'shelfMove.gripHint' | t"
                                    (pointerdown)="drag.start($event, book.id)" (click)="openMove(book.id)" (keydown)="moveKey($event, book.id)">
                                <svg libIcon="grip"></svg>
                            </button>
                        }
                    </li>
                }
            </ul>
            @if (loading()) {
                <p class="browse-more" role="status">{{ 'browse.loadingMore' | t }}</p>
            }
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        } @else if (loading() || !loaded()) {
            <lib-skeleton [rows]="6" label="browse.loading" />
        } @else {
            <p class="empty large" libEmpty="book">
                {{ (sort() === 'number' ? 'browse.emptyNumbered' : emptyText()) | t }}
                <ng-content select="[emptyExtra]" />
            </p>
        }
        @if (error() && items().length) {
            <p class="form-error" role="alert">{{ error() }} <button type="button" class="button" (click)="more()">{{ 'common.retry' | t }}</button></p>
        }
        @if (selected().size) {
            <div class="selection-dock">
                <div class="selection-bar" role="region" [attr.aria-label]="'select.bar' | t">
                    <button type="button" class="button icon-only" [attr.aria-label]="'select.clear' | t" [title]="'select.clearHint' | t" (click)="clear()">
                        <svg libIcon="close"></svg>
                    </button>
                    <span class="selection-count" role="status">{{ 'select.count' | t: { count: i18n.number(selected().size) } }}</span>
                    <button type="button" class="button quiet" [title]="'select.allHint' | t" (click)="selectAll()">{{ 'select.all' | t }}</button>
                    @if (canTag()) {
                        <button type="button" class="button primary" [title]="'select.tagsHint' | t" (click)="dialog.set('selection')">
                            <svg libIcon="tag"></svg><span class="tool-label">{{ 'select.tags' | t }}</span>
                        </button>
                    }
                    @if (canShelve()) {
                        <button type="button" class="button" [class.primary]="!canTag()" [title]="'select.shelvesHint' | t" (click)="dialog.set('shelves')">
                            <svg libIcon="shelf"></svg><span class="tool-label">{{ 'select.shelves' | t }}</span>
                        </button>
                        @if (shelf() !== null) {
                            <button type="button" class="button" [title]="'select.offShelfHint' | t" [disabled]="moving()" (click)="takeOff()">
                                <svg libIcon="minus"></svg><span class="tool-label">{{ 'select.offShelf' | t }}</span>
                            </button>
                        }
                    }
                </div>
            </div>
        }
        @if (dialog() === 'selection') {
            <lib-selection-tags [books]="selectedIds()" (saved)="clear()" (closed)="dialog.set(null)" />
        } @else if (dialog() === 'shelves') {
            <lib-shelf-picker [books]="selectedIds()" (changed)="shelfChanged($event)" (closed)="dialog.set(null)" />
        } @else if (dialog() === 'move') {
            <lib-shelf-move [heading]="moveHeading()" [place]="movePlace()" [last]="moveLast()" [busy]="moving()" (move)="moveBy($event)" (closed)="closeMove()" />
        } @else if (dialog() === 'scope') {
            <lib-scope-tags [scope]="{ library: library()!, tag: tag(), q: query(), folder: folder() }" [subtitle]="label()" (closed)="dialog.set(null)" />
        }
        <p class="visually-hidden" role="status">{{ announcement() }}</p>
    `,
    host: {
        '(pointerdown)': 'pressed($event)',
        '(pointerup)': 'released()',
        '(pointercancel)': 'released()',
        '(pointermove)': 'moved($event)',
        '(document:keydown.escape)': 'escape($event)',
    },
})
export class BookList {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);
    private readonly state = inject(BrowseState);
    private readonly shelves = inject(ShelfStore);
    private readonly toasts = inject(ToastService);
    private readonly injector = inject(Injector);
    private readonly list = viewChild<ElementRef<HTMLElement>>('list');

    /** The library's books; null on a shelf (its books come from every library). */
    readonly library = input<number | null>(null);
    /** A shelf's books instead of a library's (agents/docs/features/shelves.md). */
    readonly shelf = input<number | null>(null);
    /** Only books with this tag or one under it; null: every book of the library. */
    readonly tag = input<number | null>(null);
    /** Only books matching this search (the search language); null or empty: no search. */
    readonly query = input<string | null>(null);
    /** Only books with a file directly in this folder (the folder view, agents/docs/features/folders.md); null: any. */
    readonly folder = input<number | null>(null);
    readonly sorts = input<readonly BookSort[]>(['title', 'author', 'added']);
    readonly sortScope = input<SortScope>('library');
    /** The prefix of the sorts' texts (a shelf's "date added" is when it was put on the shelf). */
    readonly sortLabels = input('browse.sort.');
    /** The list's accessible name. */
    readonly label = input.required<string>();
    /** How many books the list has in all (`aria-setsize`); -1 when not known. */
    readonly setSize = input(-1);
    /** The key of the text shown when there are no books. */
    readonly emptyText = input('browse.empty');

    protected readonly view = signal<BrowseView>(this.state.view());
    /** The sort picked here; until then the one remembered for the scope. */
    private readonly picked = signal<BookSort | null>(null);
    protected readonly sort = computed(() => this.picked() ?? this.state.sort(this.sortScope(), this.sorts()));
    protected readonly items = signal<BookListItem[]>([]);
    private readonly next = signal<string | null>(null);
    protected readonly loading = signal(false);
    protected readonly loaded = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly canTag = computed(() => this.session.can(Rights.tagBooks));
    protected readonly canShelve = computed(() => this.session.can(Rights.shelves));
    protected readonly canSelect = computed(() => this.canTag() || this.canShelve());
    /** A shelf in its own order: books have grips. */
    protected readonly ordering = computed(() => this.shelf() !== null && this.sort() === 'shelf' && this.canShelve());
    protected readonly selected = signal<ReadonlySet<number>>(new Set());
    protected readonly selectedIds = computed(() => [...this.selected()]);
    protected readonly dialog = signal<'selection' | 'scope' | 'shelves' | 'move' | null>(null);
    /** The books the Move dialog moves. */
    private readonly moveIds = signal<number[]>([]);
    protected readonly moving = signal(false);
    /** Read out after a move by the keys or the dialog. */
    protected readonly announcement = signal('');
    /** Where a Shift-click range starts: the book last toggled. */
    private anchor: number | null = null;
    private press: { timer: ReturnType<typeof setTimeout>; x: number; y: number } | null = null;
    /** A long press selected: the click the finger's release makes is not an open. */
    private swallowClick = false;
    private readonly key = computed(() =>
        `books|${this.library() ?? ''}|${this.shelf() ?? ''}|${this.tag() ?? ''}|${this.sort()}|${this.query() ?? ''}|${this.folder() ?? ''}`);

    protected readonly window = new ListWindow({
        element: () => this.list()?.nativeElement,
        count: () => this.items().length,
        grid: () => this.view() === 'grid',
        minVar: '--card-min',
        minDefault: 140,
        item: '.book',
    });
    protected readonly shown = computed(() => {
        const { start, end } = this.window.range();
        return this.items().slice(start, end).map((book, i) => ({ book, index: start + i }));
    });
    private requested = 0;

    protected readonly drag = new ShelfDrag({
        list: () => this.list()?.nativeElement,
        grid: () => this.view() === 'grid',
        moving: id => this.movingWith(id),
        drop: (ids, target) => void this.dropped(ids, target),
    });

    protected readonly moveHeading = computed(() => {
        const ids = this.moveIds();
        const book = ids.length === 1 ? this.items().find(b => b.id === ids[0]) : undefined;
        return book ? this.i18n.t('shelfMove.heading.one', { title: this.title(book) }) : this.i18n.t('shelfMove.heading', { count: this.i18n.number(ids.length) });
    });
    protected readonly movePlace = computed(() => {
        const ids = new Set(this.moveIds());
        return this.items().findIndex(b => ids.has(b.id)) + 1;
    });
    protected readonly moveLast = computed(() => {
        const ids = new Set(this.moveIds());
        const items = this.items();
        return this.next() === null && items.slice(items.length - ids.size).every(b => ids.has(b.id));
    });

    constructor() {
        const host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
        const click = (event: MouseEvent) => this.clicked(event);
        host.addEventListener('click', click, true);
        inject(DestroyRef).onDestroy(() => host.removeEventListener('click', click, true));

        // Another library, tag or sort: the list again, from what was left (on the way back) or from its first page.
        effect(() => {
            const key = this.key();
            untracked(() => this.start(key));
        });

        // Near the end of what is loaded: the next page.
        effect(() => {
            const { end } = this.window.range();
            const count = this.items().length;
            if (count && end >= count - this.window.perRow() * 3 && this.next() && !untracked(this.loading) && !untracked(this.error))
                untracked(() => void this.more());
        });

        // Each render: the row's height (cards change with the width and the density) and, once known, the way back.
        afterRenderEffect(() => {
            this.shown();
            this.view();
            this.window.measure();
        });

        inject(DestroyRef).onDestroy(() => {
            this.drag.destroy();
            if (this.items().length)
                this.state.keep({ key: this.key(), items: this.items(), next: this.next(), offset: this.window.offset() });
        });
    }

    protected toggle(id: number): void {
        this.anchor = id;
        this.selected.update(set => {
            const next = new Set(set);
            if (!next.delete(id) && next.size < MAX_SELECTION)
                next.add(id);
            return next;
        });
    }

    protected clear(): void {
        this.selected.set(new Set());
        this.anchor = null;
    }

    protected selectAll(): void {
        this.selected.set(new Set(this.items().slice(0, MAX_SELECTION).map(book => book.id)));
    }

    private bookOf(event: Event): number | null {
        const target = event.target as Element | null;
        if (!target?.closest('a.book-link'))
            return null;
        const id = Number(target.closest('li.book')?.getAttribute('data-book'));
        return Number.isInteger(id) && id > 0 ? id : null;
    }

    /**
     * Runs before the link's own handler (a capture listener on the host sees the click on its way down; stopping it
     * there keeps RouterLink from opening the book): Ctrl / Cmd and Shift, or any click while books are selected, select
     * instead of opening.
     */
    protected clicked(event: MouseEvent): void {
        const id = this.bookOf(event);
        if (id === null || !this.canSelect())
            return;
        if (this.swallowClick) {
            this.swallowClick = false;
            event.preventDefault();
            event.stopPropagation();
            return;
        }
        if (event.shiftKey && this.anchor !== null) {
            event.preventDefault();
            event.stopPropagation();
            const ids = this.items().map(book => book.id);
            const [from, to] = [ids.indexOf(this.anchor), ids.indexOf(id)].sort((a, b) => a - b);
            if (from >= 0)
                this.selected.update(set => new Set([...set, ...ids.slice(from, to + 1)].slice(0, MAX_SELECTION)));
            this.anchor = id;
        } else if (event.ctrlKey || event.metaKey || this.selected().size) {
            event.preventDefault();
            event.stopPropagation();
            this.toggle(id);
        }
    }

    /** A finger resting on a book selects it (iOS gives links no `contextmenu`: a timer, cancelled by a move or a lift). */
    protected pressed(event: PointerEvent): void {
        // A long press may end without a click (the browser's own long press): the next touch starts afresh.
        this.swallowClick = false;
        if (event.pointerType !== 'touch' || !this.canSelect())
            return;
        const id = this.bookOf(event);
        if (id === null)
            return;
        this.released();
        this.press = {
            x: event.clientX, y: event.clientY, timer: setTimeout(() => {
                this.press = null;
                this.swallowClick = true;
                this.toggle(id);
            }, LONG_PRESS),
        };
    }

    protected moved(event: PointerEvent): void {
        if (this.press && Math.hypot(event.clientX - this.press.x, event.clientY - this.press.y) > 10)
            this.released();
    }

    protected released(): void {
        if (this.press)
            clearTimeout(this.press.timer);
        this.press = null;
    }

    protected escape(event: Event): void {
        const target = event.target instanceof Element ? event.target : null;
        if (!this.selected().size || this.dialog() || target?.closest('input, textarea, select, dialog'))
            return;
        this.clear();
    }

    protected chooseSort(event: Event): void {
        const sort = valueOf(event) as BookSort;
        this.state.setSort(sort, this.sortScope());
        this.picked.set(sort);
    }

    protected chooseView(view: BrowseView): void {
        this.state.setView(view);
        this.view.set(view);
        this.window.reset();
    }

    protected title(book: BookListItem): string {
        return book.title || book.fileName || this.i18n.t('browse.untitled');
    }

    protected series(book: BookListItem): string {
        return book.seriesNumber != null ? this.i18n.t('browse.seriesNumber', { series: book.series!, number: book.seriesNumber }) : book.series!;
    }

    protected cover(book: BookListItem): string {
        return coverUrl(book.id, this.view() === 'grid' ? 'm' : 's', book.coverVersion);
    }

    /** The next page, by key. */
    protected async more(): Promise<void> {
        if (this.loading())
            return;
        const asked = this.requested;
        this.loading.set(true);
        this.error.set(null);
        try {
            const shelf = this.shelf();
            const page = shelf !== null
                ? await this.api.shelfBooks(shelf, { sort: this.sort(), after: this.next(), limit: this.pageSize() })
                : await this.api.books({
                    library: this.library()!, sort: this.sort(), tag: this.tag(), q: this.query(), folder: this.folder(), after: this.next(), limit: this.pageSize(),
                });
            if (asked !== this.requested)
                return;
            // A book moved on a shelf since the page before may come again: kept once.
            this.items.update(items => {
                const have = new Set(items.map(book => book.id));
                return [...items, ...page.items.filter(book => !have.has(book.id))];
            });
            this.next.set(page.next);
        } catch (error) {
            if (asked === this.requested)
                this.error.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.requested) {
                this.loading.set(false);
                this.loaded.set(true);
            }
        }
    }

    /** The selected books (in the list's order) when `id` is one of them, else that book. */
    protected movingWith(id: number): number[] {
        const selected = this.selected();
        return selected.has(id) ? inListOrder(this.items(), selected) : [id];
    }

    /** A click on a grip (not the end of a drag): Move. */
    protected openMove(id: number): void {
        if (this.drag.consumeClick())
            return;
        this.moveIds.set(this.movingWith(id));
        this.dialog.set('move');
    }

    protected closeMove(): void {
        this.dialog.set(null);
        this.moveIds.set([]);
    }

    protected moveBy(move: ShelfMove): void {
        void this.moveOnShelf(this.moveIds(), move);
    }

    protected async moveKey(event: KeyboardEvent, id: number): Promise<void> {
        const move = MOVE_KEYS[event.key];
        if (!move)
            return;
        event.preventDefault();
        if (await this.moveOnShelf(this.movingWith(id), move))
            this.refocus(id);
    }

    private async moveOnShelf(ids: number[], move: ShelfMove): Promise<boolean> {
        ids = inListOrder(this.items(), ids);
        const index = moveIndex(this.items(), ids, move);
        // Down from the last of the whole shelf: nowhere to go.
        if (index === null || (move === 'down' && index > this.items().length - ids.length && this.next() === null))
            return false;
        return this.placeAt(ids, move === 'end' ? null : index);
    }

    private async dropped(ids: number[], target: DropTarget): Promise<void> {
        ids = inListOrder(this.items(), ids);
        const at = this.items().filter(book => !ids.includes(book.id)).findIndex(book => book.id === target.id);
        if (at >= 0)
            await this.placeAt(ids, target.after ? at + 1 : at);
    }

    /** Puts `ids` before the `index`-th of the other books (null, or past the last of the shelf: at its end). */
    private async placeAt(ids: number[], index: number | null): Promise<boolean> {
        let before: number | null = null;
        if (index !== null) {
            const others = () => this.items().filter(book => !ids.includes(book.id));
            // After the last loaded: the book that comes next is on the next page.
            if (index >= others().length && this.next())
                await this.more();
            before = others()[index]?.id ?? null;
        }
        return this.send(ids, before);
    }

    /** Shows the move at once, then asks the server; a refusal puts the list back. */
    private async send(ids: number[], before: number | null): Promise<boolean> {
        const shelf = this.shelf();
        if (shelf === null || this.moving() || !ids.length)
            return false;
        const was = this.items();
        const now = movedLocally(was, ids, before, this.next() === null);
        this.items.set(now);
        this.moving.set(true);
        try {
            await this.api.moveOnShelf(shelf, ids, before);
            const place = now.findIndex(book => book.id === ids[0]);
            this.announcement.set(place < 0
                ? this.i18n.t('shelfMove.movedEnd')
                : this.i18n.t('shelfMove.moved', { place: this.i18n.number(place + 1) }));
            return true;
        } catch (error) {
            this.items.set(was);
            this.toasts.error(errorText(this.i18n, error));
            return false;
        } finally {
            this.moving.set(false);
        }
    }

    /** The grip of the book moved by the keys keeps the focus (it was drawn again elsewhere, or out of the window). */
    private refocus(id: number): void {
        afterNextRender(() => {
            const list = this.list()?.nativeElement;
            const grip = list?.querySelector<HTMLElement>(`[data-book="${id}"] .book-move`);
            if (grip) {
                grip.focus({ preventScroll: true });
                grip.scrollIntoView?.({ block: 'nearest' });
            } else if (list && this.items()[0]?.id === id) {
                scrollTo({ top: list.getBoundingClientRect().top + scrollY - 80 });
                afterNextRender(() => list.querySelector<HTMLElement>(`[data-book="${id}"] .book-move`)?.focus(), { injector: this.injector });
            }
        }, { injector: this.injector });
    }

    /** Takes the selected books off the shelf. */
    protected async takeOff(): Promise<void> {
        const shelf = this.shelf();
        const ids = inListOrder(this.items(), this.selected());
        if (shelf === null || !ids.length)
            return;
        this.moving.set(true);
        try {
            const info = await this.api.removeFromShelf(shelf, ids);
            this.shelves.put(info);
            const gone = new Set(ids);
            this.items.update(items => items.filter(book => !gone.has(book.id)));
            this.clear();
            this.toasts.show(this.i18n.t('select.offShelfDone', { count: this.i18n.number(ids.length), name: info.name }));
        } catch (error) {
            this.toasts.error(errorText(this.i18n, error));
        } finally {
            this.moving.set(false);
        }
    }

    /** Add to shelf changed a shelf: when it is this one, its books again. */
    protected shelfChanged(id: number): void {
        if (id === this.shelf())
            this.reload();
    }

    /** The list again from its first page (nothing kept for the way back). */
    reload(): void {
        this.state.forget(this.key());
        this.start(this.key());
    }

    private start(key: string): void {
        // Answers to the earlier list's questions are dropped; another list, another selection.
        this.requested++;
        this.clear();
        this.loading.set(false);
        this.error.set(null);
        const kept = this.state.restore<BookListItem>(key);
        if (kept) {
            this.items.set(kept.items);
            this.next.set(kept.next);
            this.loaded.set(true);
            this.window.restore(kept.offset);
            return;
        }
        this.items.set([]);
        this.next.set(null);
        this.loaded.set(false);
        this.window.restore(null);
        scrollTo({ top: 0 });
        void this.more();
    }

    private pageSize(): number {
        const size = Number(this.session.session()?.preferences['ui.pageSize']);
        return Number.isInteger(size) && size > 0 ? size : DEFAULT_PAGE;
    }
}
