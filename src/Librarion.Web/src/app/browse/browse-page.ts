import {
    ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, afterRenderEffect, computed, effect, inject, signal, untracked, viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, coverUrl } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { BookListItem, BookSort } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { bookWindow, gridColumns } from './book-window';
import { BrowseState, BrowseView } from './browse-state';

const SORTS: readonly BookSort[] = ['title', 'author', 'added'];
/** Books asked for at a time when the account's `ui.pageSize` says nothing. */
const DEFAULT_PAGE = 50;

/**
 * The library (the home page; agents/docs/features/browse.md, D-5.5): its books as a grid of covers or a dense list,
 * sorted by title, author or newest, the choices remembered per browser. Pages come by key as the end nears; only the
 * rows in view are drawn (`bookWindow`), so a library of millions stays light. Leaving and coming back shows the list
 * as it was, scrolled where it was (`BrowseState`).
 */
@Component({
    selector: 'lib-browse-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'browse-page' },
    template: `
        @if (library(); as library) {
            <header class="browse-head">
                <div class="browse-title">
                    <h1>{{ library.name }}</h1>
                    @if (library.bookCount != null) {
                        <p class="browse-count">{{ 'browse.count' | t: { count: number(library.bookCount) } }}</p>
                    }
                </div>
                <div class="browse-tools">
                    <label class="browse-sort">
                        <span class="visually-hidden">{{ 'browse.sort' | t }}</span>
                        <select [title]="'browse.sortHint' | t" (change)="chooseSort($event)">
                            @for (choice of sorts; track choice) {
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
                <ul #list class="books" [class.grid]="view() === 'grid'" [class.list]="view() === 'list'" [attr.aria-label]="'browse.books' | t: { name: library.name }"
                    [style.grid-template-columns]="view() === 'grid' ? 'repeat(' + columns() + ', minmax(0, 1fr))' : null"
                    [style.padding-top.px]="window().before" [style.padding-bottom.px]="window().after">
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
                    {{ 'browse.empty' | t }}
                    @if (canManage()) {
                        <a routerLink="/admin/libraries">{{ 'browse.emptyAdmin' | t }}</a>
                    }
                </p>
            }
            @if (error() && items().length) {
                <p class="form-error" role="alert">{{ error() }} <button type="button" class="button" (click)="more()">{{ 'common.retry' | t }}</button></p>
            }
        } @else if (librariesLoaded()) {
            <h1>{{ 'browse.noLibraryTitle' | t }}</h1>
            <p class="empty large" libEmpty="library">
                {{ 'browse.noLibrary' | t }}
                @if (canManage()) {
                    <a routerLink="/admin/libraries">{{ 'browse.emptyAdmin' | t }}</a>
                }
            </p>
        } @else {
            <lib-skeleton [rows]="6" label="browse.loading" />
        }
    `,
})
export class BrowsePage {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);
    private readonly libraries = inject(LibraryContext);
    private readonly state = inject(BrowseState);
    private readonly injector = inject(Injector);
    private readonly list = viewChild<ElementRef<HTMLElement>>('list');

    protected readonly sorts = SORTS;
    protected readonly view = signal<BrowseView>(this.state.view());
    protected readonly sort = signal<BookSort>(this.state.sort());
    protected readonly items = signal<BookListItem[]>([]);
    private readonly next = signal<string | null>(null);
    protected readonly loading = signal(false);
    protected readonly loaded = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly library = this.libraries.current;
    protected readonly librariesLoaded = signal(false);
    protected readonly canManage = computed(() => this.session.canAny(Rights.manageLibraries, Rights.manageTenants));
    private readonly key = computed(() => `${this.library()?.id ?? ''}|${this.sort()}`);
    protected readonly setSize = computed(() => this.library()?.bookCount ?? -1);

    // The drawn window: where the list is, how wide (columns), how tall a row is.
    private readonly top = signal(0);
    private readonly viewport = signal(typeof innerHeight === 'number' ? innerHeight : 800);
    protected readonly columns = signal(4);
    private readonly rowHeight = signal(0);
    protected readonly window = computed(() =>
        bookWindow(this.top(), this.viewport(), this.rowHeight(), this.view() === 'grid' ? this.columns() : 1, this.items().length));
    protected readonly shown = computed(() => {
        const { start, end } = this.window();
        return this.items().slice(start, end).map((book, i) => ({ book, index: start + i }));
    });
    /** Where to scroll back to once the restored list can be that tall. */
    private restoreTo: number | null = null;
    private requested = 0;

    constructor() {
        void this.libraries.load().finally(() => this.librariesLoaded.set(true));

        // Another library or sort: the list again, from what was left (on the way back) or from its first page.
        effect(() => {
            const key = this.key();
            untracked(() => this.start(key));
        });

        // Near the end of what is loaded: the next page.
        effect(() => {
            const { end } = this.window();
            const count = this.items().length;
            if (count && end >= count - this.columns() * 3 && this.next() && !untracked(this.loading) && !untracked(this.error))
                untracked(() => void this.more());
        });

        // Each render: the row's height (cards change with the width and the density) and, once known, the way back.
        afterRenderEffect(() => {
            this.shown();
            this.view();
            this.measure();
        });

        const onScroll = () => this.schedule();
        addEventListener('scroll', onScroll, { passive: true });
        addEventListener('resize', onScroll, { passive: true });
        const destroyRef = inject(DestroyRef);
        destroyRef.onDestroy(() => {
            removeEventListener('scroll', onScroll);
            removeEventListener('resize', onScroll);
            if (this.items().length)
                this.state.keep({ key: this.key(), items: this.items(), next: this.next(), offset: Math.max(0, this.top()) });
        });
    }

    protected chooseSort(event: Event): void {
        const sort = valueOf(event) as BookSort;
        this.state.setSort(sort);
        this.sort.set(sort);
    }

    protected chooseView(view: BrowseView): void {
        this.state.setView(view);
        this.view.set(view);
        this.rowHeight.set(0);
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

    protected number(value: number): string {
        return new Intl.NumberFormat(this.i18n.language()).format(value);
    }

    /** The next page, by key. */
    protected async more(): Promise<void> {
        const library = this.library();
        if (!library || this.loading())
            return;
        const asked = this.requested;
        this.loading.set(true);
        this.error.set(null);
        try {
            const page = await this.api.books({ library: library.id, sort: this.sort(), after: this.next(), limit: this.pageSize() });
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
        const kept = this.state.restore(key);
        if (kept) {
            this.items.set(kept.items);
            this.next.set(kept.next);
            this.loaded.set(true);
            this.restoreTo = kept.offset;
            return;
        }
        this.items.set([]);
        this.next.set(null);
        this.loaded.set(false);
        this.restoreTo = null;
        if (this.library()) {
            scrollTo({ top: 0 });
            void this.more();
        }
    }

    private pageSize(): number {
        const size = Number(this.session.session()?.preferences['ui.pageSize']);
        return Number.isInteger(size) && size > 0 ? size : DEFAULT_PAGE;
    }

    private frame = 0;

    private schedule(): void {
        if (this.frame)
            return;
        this.frame = requestAnimationFrame(() => {
            this.frame = 0;
            this.measure();
        });
    }

    /** Where the list is and how its rows are: what the window needs. */
    private measure(): void {
        const list = this.list()?.nativeElement;
        if (!list)
            return;
        const style = getComputedStyle(list);
        const rect = list.getBoundingClientRect();
        this.top.set(-rect.top);
        this.viewport.set(innerHeight);
        if (this.view() === 'grid') {
            const gap = parseFloat(style.columnGap) || 0;
            const width = rect.width - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0);
            const columns = gridColumns(width, parseFloat(style.getPropertyValue('--card-min')) || 140, gap);
            // The rows drawn have the old count: measured again once drawn with the new one.
            if (columns !== this.columns()) {
                this.columns.set(columns);
                return;
            }
        }
        // A row's pitch, measured between the drawn rows' tops (the gap and any rounding included); with one row drawn,
        // its height and the gap.
        const drawn = list.querySelectorAll<HTMLElement>('.book');
        const columns = this.view() === 'grid' ? this.columns() : 1;
        const rows = Math.floor((drawn.length - 1) / columns);
        let height = 0;
        if (rows >= 1)
            height = (drawn[rows * columns].getBoundingClientRect().top - drawn[0].getBoundingClientRect().top) / rows;
        else if (drawn[0]?.offsetHeight)
            height = drawn[0].offsetHeight + (parseFloat(style.rowGap) || 0);
        if (height > 0 && Math.abs(height - this.rowHeight()) > 0.5)
            this.rowHeight.set(height);
        if (this.restoreTo !== null && this.rowHeight() > 0) {
            const offset = this.restoreTo;
            this.restoreTo = null;
            afterNextRender(() => scrollTo({ top: list.getBoundingClientRect().top + scrollY + offset }), { injector: this.injector });
        }
    }
}
