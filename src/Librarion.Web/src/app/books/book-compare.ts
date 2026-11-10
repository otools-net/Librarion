import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api, coverUrl } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookDetail } from '../core/models';
import { Sheet } from '../shared/sheet';
import { FieldCatalog } from '../tags/field-catalog';

/** A value taken from one book: the kept one's unless another is chosen. */
type Choice = 'title' | 'cover' | 'description' | 'rating';

/**
 * Compare and merge (stage 6 chunk 12, `library.merge`; agents/docs/features/merging.md#merging-books-built-stage-6-chunk-12):
 * the books side by side on a desktop, one under the other on a phone, rows whose values differ marked. One is kept
 * (the one with most metadata, else the oldest, at first); the others chosen are merged into it: their files, tags and
 * identifiers move, and the cover, title, description and rating come from the book picked for each. Books of another
 * library can't be merged here.
 */
@Component({
    selector: 'lib-book-compare',
    imports: [TranslatePipe, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'compare.heading' | t" [subtitle]="'compare.subtitle' | t" [wide]="true" (closed)="closed.emit()">
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            @if (books().length) {
                <div class="compare-books">
                    @for (book of books(); track book.id) {
                        @let kept = book.id === keep();
                        @let other = !sameLibrary(book);
                        <section class="compare-book" [class.kept]="kept" [attr.aria-label]="title(book)">
                            <div class="compare-head">
                                <label class="compare-pick">
                                    <input type="radio" name="compare-keep" [checked]="kept" [disabled]="busy() || other" (change)="keepBook(book.id)">
                                    {{ 'compare.keep' | t }}
                                </label>
                                @if (!kept) {
                                    <label class="compare-pick">
                                        <input type="checkbox" [checked]="included().has(book.id)" [disabled]="busy() || other" (change)="toggle(book.id)">
                                        {{ 'compare.include' | t }}
                                    </label>
                                }
                            </div>
                            @if (other) {
                                <p class="field-hint">{{ 'compare.otherLibrary' | t }}</p>
                            }
                            <div class="compare-row" [class.differs]="differs('cover')">
                                <span class="compare-cover"><img [src]="cover(book)" alt="" width="240" height="360" decoding="async"></span>
                                @if (differs('cover')) {
                                    <label class="compare-use"><input type="radio" name="compare-cover" [checked]="isFrom('cover', book.id)" [disabled]="!usable(book)" (change)="use('cover', book.id)"> {{ 'compare.useCover' | t }}</label>
                                }
                            </div>
                            <div class="compare-row" [class.differs]="differs('title')">
                                <span class="compare-label">{{ 'compare.title' | t }}</span>
                                <span class="compare-value compare-title">{{ title(book) }}</span>
                                @if (differs('title')) {
                                    <label class="compare-use"><input type="radio" name="compare-title" [checked]="isFrom('title', book.id)" [disabled]="!usable(book)" (change)="use('title', book.id)"> {{ 'compare.useThis' | t }}</label>
                                }
                            </div>
                            <div class="compare-row" [class.differs]="differsText(authors)">
                                <span class="compare-label">{{ 'compare.authors' | t }}</span>
                                <span class="compare-value">{{ authors(book) || '—' }}</span>
                            </div>
                            <div class="compare-row" [class.differs]="differsText(series)">
                                <span class="compare-label">{{ 'compare.series' | t }}</span>
                                <span class="compare-value">{{ series(book) || '—' }}</span>
                            </div>
                            <div class="compare-row" [class.differs]="differsText(tags)">
                                <span class="compare-label">{{ 'compare.tags' | t }}</span>
                                <span class="compare-value">{{ tags(book) || '—' }}</span>
                            </div>
                            <div class="compare-row">
                                <span class="compare-label">{{ 'compare.files' | t }}</span>
                                <span class="compare-value">
                                    @for (file of book.files; track file.id) {
                                        <span class="compare-file">{{ file.format.toUpperCase() }} · {{ i18n.size(file.size) }} · {{ file.name }}</span>
                                    }
                                </span>
                            </div>
                            <div class="compare-row" [class.differs]="differsText(identifiers)">
                                <span class="compare-label">{{ 'compare.identifiers' | t }}</span>
                                <span class="compare-value">{{ identifiers(book) || '—' }}</span>
                            </div>
                            <div class="compare-row" [class.differs]="differs('description')">
                                <span class="compare-label">{{ 'compare.description' | t }}</span>
                                <span class="compare-value compare-description">{{ book.description || '—' }}</span>
                                @if (differs('description')) {
                                    <label class="compare-use"><input type="radio" name="compare-description" [checked]="isFrom('description', book.id)" [disabled]="!usable(book)" (change)="use('description', book.id)"> {{ 'compare.useThis' | t }}</label>
                                }
                            </div>
                            <div class="compare-row" [class.differs]="differs('rating')">
                                <span class="compare-label">{{ 'compare.rating' | t }}</span>
                                <span class="compare-value">{{ book.rating != null ? i18n.t('compare.stars', { rating: book.rating / 2 }) : '—' }}</span>
                                @if (differs('rating')) {
                                    <label class="compare-use"><input type="radio" name="compare-rating" [checked]="isFrom('rating', book.id)" [disabled]="!usable(book)" (change)="use('rating', book.id)"> {{ 'compare.useThis' | t }}</label>
                                }
                            </div>
                            <div class="compare-row">
                                <span class="compare-label">{{ 'compare.added' | t }}</span>
                                <span class="compare-value">{{ added(book) }}</span>
                            </div>
                        </section>
                    }
                </div>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                <button type="button" class="button primary" [title]="'compare.mergeHint' | t" [disabled]="busy() || !sources().length" (click)="merge()">
                    {{ sources().length ? ('compare.merge' | t: { count: sources().length + 1 }) : ('compare.mergeNone' | t) }}
                </button>
            </div>
        </lib-sheet>
    `,
})
export class BookCompare implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly catalog = inject(FieldCatalog);

    /** The books compared (two or more). */
    readonly bookIds = input.required<number[]>();
    /** The kept book, after the merge. */
    readonly merged = output<BookDetail>();
    readonly closed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly books = signal<BookDetail[]>([]);
    protected readonly keep = signal<number | null>(null);
    protected readonly included = signal<ReadonlySet<number>>(new Set());
    protected readonly from = signal<Partial<Record<Choice, number>>>({});
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    private readonly kept = computed(() => this.books().find(b => b.id === this.keep()) ?? null);
    /** The books merged into the kept one: chosen, of its library. */
    protected readonly sources = computed(() => this.books().filter(b => b.id !== this.keep() && this.included().has(b.id) && this.sameLibrary(b)).map(b => b.id));

    protected readonly authors = (book: BookDetail) => book.tags.filter(t => t.field === 'author').map(t => t.name).join(', ');
    protected readonly series = (book: BookDetail) => book.tags.filter(t => t.field === 'series')
        .map(t => t.number != null ? this.i18n.t('browse.seriesNumber', { series: t.name, number: t.number }) : t.name).join(', ');
    protected readonly tags = (book: BookDetail) => book.tags.filter(t => t.field !== 'author' && t.field !== 'series')
        .map(t => this.catalog.value(t.field, t.name)).sort().join(', ');
    protected readonly identifiers = (book: BookDetail) => book.identifiers.map(i => `${i.key}: ${i.value}`).sort().join(', ');

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        try {
            const books = await Promise.all(this.bookIds().map(id => this.api.book(id)));
            // The kept book at first: the one with most metadata, else the oldest.
            const score = (b: BookDetail) => b.tags.length + b.identifiers.length + b.files.length + (b.title ? 1 : 0) + (b.description ? 1 : 0) + (b.rating != null ? 1 : 0);
            const keep = [...books].sort((a, b) => score(b) - score(a) || a.id - b.id)[0];
            this.books.set(books);
            this.keep.set(keep.id);
            this.included.set(new Set(books.filter(b => b.id !== keep.id && b.libraryId === keep.libraryId).map(b => b.id)));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected title(book: BookDetail): string {
        return book.title || book.fileName || this.i18n.t('browse.untitled');
    }

    protected cover(book: BookDetail): string {
        return coverUrl(book.id, 'm', book.coverVersion);
    }

    protected added(book: BookDetail): string {
        return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(book.added));
    }

    protected sameLibrary(book: BookDetail): boolean {
        return book.libraryId === this.kept()?.libraryId;
    }

    /** A book whose values may be taken: the kept one or one merged into it. */
    protected usable(book: BookDetail): boolean {
        return !this.busy() && (book.id === this.keep() || this.sources().includes(book.id));
    }

    protected differs(choice: Choice): boolean {
        const values = this.books().map(b => choice === 'cover' ? `${b.id}` : choice === 'title' ? this.title(b) : String(b[choice] ?? ''));
        return new Set(values).size > 1;
    }

    protected differsText(value: (book: BookDetail) => string): boolean {
        return new Set(this.books().map(value)).size > 1;
    }

    protected isFrom(choice: Choice, id: number): boolean {
        return (this.from()[choice] ?? this.keep()) === id;
    }

    protected use(choice: Choice, id: number): void {
        this.from.update(from => ({ ...from, [choice]: id }));
    }

    protected keepBook(id: number): void {
        this.keep.set(id);
        const library = this.kept()?.libraryId;
        this.included.set(new Set(this.books().filter(b => b.id !== id && b.libraryId === library).map(b => b.id)));
        this.from.set({});
    }

    protected toggle(id: number): void {
        this.included.update(set => {
            const next = new Set(set);
            if (!next.delete(id))
                next.add(id);
            return next;
        });
        // A value can't come from a book left out.
        this.from.update(from => Object.fromEntries(Object.entries(from).filter(([, book]) => book !== id)));
    }

    protected async merge(): Promise<void> {
        const keep = this.keep();
        if (keep === null)
            return;
        const from = this.from();
        const pick = (choice: Choice) => from[choice] !== undefined && from[choice] !== keep ? from[choice] : null;
        this.busy.set(true);
        this.error.set(null);
        try {
            const book = await this.api.mergeBooks({
                targetId: keep, sourceIds: this.sources(),
                titleFrom: pick('title'), descriptionFrom: pick('description'), ratingFrom: pick('rating'), coverFrom: pick('cover'),
            });
            this.merged.emit(book);
            this.sheet()?.close();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}

