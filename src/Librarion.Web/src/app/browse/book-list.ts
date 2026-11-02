import {
    ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterRenderEffect, computed, effect, inject, input, signal, untracked, viewChild,
} from '@angular/core';
import { Api, coverUrl } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookListItem, BookSort } from '../core/models';
import { SessionService } from '../core/session.service';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { BrowseState, BrowseView, SortScope } from './browse-state';
import { ListWindow } from './list-window';

/** Books asked for at a time when the account's `ui.pageSize` says nothing. */
const DEFAULT_PAGE = 50;

/**
 * Books of a library, all or those with a tag (agents/docs/features/browse.md, D-5.5): a grid of covers or a dense
 * list, the sort and view chosen in its tools and remembered per browser. Pages come by key as the end nears; only the
 * rows in view are drawn (`ListWindow`), so millions stay light. Leaving and coming back shows the list as it was,
 * scrolled where it was (`BrowseState`). The page's heading is projected beside the tools; `[emptyExtra]` after the
 * empty text.
 */
@Component({
    selector: 'lib-book-list',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <header class="browse-head">
            <div class="browse-title"><ng-content /></div>
            <div class="browse-tools">
                <label class="browse-sort">
                    <span class="visually-hidden">{{ 'browse.sort' | t }}</span>
                    <select [title]="'browse.sortHint' | t" (change)="chooseSort($event)">
                        @for (choice of sorts(); track choice) {
                            <option [value]="choice" [selected]="choice === sort()">{{ ('browse.sort.' + choice) | t }}</option>
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
            <ul #list class="books" [class.grid]="view() === 'grid'" [class.list]="view() === 'list'" [attr.aria-label]="label()"
                [style.grid-template-columns]="view() === 'grid' ? 'repeat(' + window.columns() + ', minmax(0, 1fr))' : null"
                [style.padding-top.px]="window.range().before" [style.padding-bottom.px]="window.range().after">
                @for (entry of shown(); track entry.book.id) {
                    @let book = entry.book;
                    <li class="book" [attr.aria-posinset]="entry.index + 1" [attr.aria-setsize]="setSize()">
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
    `,
})
export class BookList {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);
    private readonly state = inject(BrowseState);
    private readonly list = viewChild<ElementRef<HTMLElement>>('list');

    readonly library = input.required<number>();
    /** Only books with this tag or one under it; null: every book of the library. */
    readonly tag = input<number | null>(null);
    readonly sorts = input<readonly BookSort[]>(['title', 'author', 'added']);
    readonly sortScope = input<SortScope>('library');
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
    private readonly key = computed(() => `books|${this.library()}|${this.tag() ?? ''}|${this.sort()}`);

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

    constructor() {
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
            if (this.items().length)
                this.state.keep({ key: this.key(), items: this.items(), next: this.next(), offset: this.window.offset() });
        });
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
        return coverUrl(book.id, this.view() === 'grid' ? 'm' : 's');
    }

    /** The next page, by key. */
    protected async more(): Promise<void> {
        if (this.loading())
            return;
        const asked = this.requested;
        this.loading.set(true);
        this.error.set(null);
        try {
            const page = await this.api.books({ library: this.library(), sort: this.sort(), tag: this.tag(), after: this.next(), limit: this.pageSize() });
            if (asked !== this.requested)
                return;
            this.items.update(items => [...items, ...page.items]);
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

    private start(key: string): void {
        // Answers to the earlier list's questions are dropped.
        this.requested++;
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
