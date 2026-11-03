import { Location } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, coverUrl, fileUrl } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { BookDetail, BookDuplicate, BookFile, BookListItem, BookTag } from '../core/models';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { FieldCatalog } from '../tags/field-catalog';

/** The book's tags of one field (or the plain tags, `field` null), in the order the server gave them. */
interface FieldGroup {
    field: string | null;
    tags: BookTag[];
}

/**
 * A book (`books/:id`; agents/docs/features/book-page.md, D-5.6): its cover, title (from the file name when the
 * metadata gave none, marked), authors and series linking to their pages, the other fields as chips, the description,
 * its files with Download (when the session may download in its library), details (added, identifiers, imported
 * properties), and other books that may be the same one (read only until merging comes). A merged book goes to the
 * book it now lives in.
 */
@Component({
    selector: 'lib-book-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'book-page' },
    template: `
        <button type="button" class="button quiet book-back" [title]="'book.backHint' | t" (click)="back()">
            <svg libIcon="chevron-left"></svg>{{ 'book.back' | t }}
        </button>
        @if (book(); as book) {
            <article class="book-sheet" aria-labelledby="book-title">
                <div class="book-head">
                    <span class="book-page-cover">
                        <img [src]="cover(book.id, 'l')" alt="" width="600" height="900" decoding="async">
                    </span>
                    <div class="book-main">
                        @if (libraryName(); as name) {
                            <p class="tag-kind"><svg libIcon="library"></svg>{{ name }}</p>
                        }
                        <h1 id="book-title" [class.file-title]="!book.title" [attr.title]="book.title ? null : ('book.fileTitleHint' | t)">{{ title(book) }}</h1>
                        @if (authors().length) {
                            <p class="book-by">
                                {{ 'book.by' | t }}
                                @for (author of authors(); track author.id; let last = $last) {
                                    <a [routerLink]="['/tags', author.id]">{{ author.name }}</a>@if (!last) {, }
                                }
                            </p>
                        }
                        @for (series of series(); track series.id) {
                            <p class="book-in-series">
                                <svg libIcon="series"></svg>
                                <a [routerLink]="['/tags', series.id]">{{ seriesText(series) }}</a>
                            </p>
                        }
                        @if (book.rating) {
                            <p class="book-rating" [attr.aria-label]="'book.ratingLabel' | t: { stars: i18n.number(book.rating / 2) }">
                                <svg libIcon="star"></svg>{{ 'book.rating' | t: { stars: i18n.number(book.rating / 2) } }}
                            </p>
                        }
                        @if (first(); as file) {
                            <p class="book-actions">
                                <a class="button primary" [href]="download(book, file)" download [title]="'book.downloadHint' | t: { name: file.name }">
                                    <svg libIcon="download"></svg>{{ 'book.download' | t: { format: file.format.toUpperCase() } }}
                                </a>
                            </p>
                        }
                        @if (fields().length) {
                            <dl class="book-fields">
                                @for (group of fields(); track group.field) {
                                    <div class="book-field">
                                        <dt>{{ fieldName(group) }}</dt>
                                        <dd>
                                            @for (tag of group.tags; track tag.id) {
                                                <a class="chip" [routerLink]="['/tags', tag.id]">{{ catalog.value(tag.field, tag.name) }}</a>
                                            }
                                        </dd>
                                    </div>
                                }
                            </dl>
                        }
                    </div>
                </div>

                @if (book.description) {
                    <section class="book-section" aria-labelledby="book-description">
                        <h2 id="book-description">{{ 'book.description' | t }}</h2>
                        <p class="book-description">{{ book.description }}</p>
                    </section>
                }

                <section class="book-section" aria-labelledby="book-files">
                    <h2 id="book-files">{{ 'book.files' | t }}</h2>
                    <ul class="book-files">
                        @for (file of book.files; track file.id) {
                            <li class="book-file" [class.missing]="file.missing">
                                <span class="badge">{{ file.format.toUpperCase() }}</span>
                                <span class="book-file-text">
                                    <span class="book-file-name" [title]="file.name">{{ file.name }}</span>
                                    <span class="book-file-where">
                                        {{ i18n.size(file.size) }}
                                        @if (file.archive) {
                                            · {{ 'book.inArchive' | t: { archive: file.archive } }}
                                        }
                                        @if (file.missing) {
                                            · <span class="book-file-missing">{{ 'book.fileMissing' | t }}</span>
                                        }
                                    </span>
                                </span>
                                @if (book.canDownload && !file.missing) {
                                    <a class="button icon-only" [href]="download(book, file)" download
                                       [attr.aria-label]="'book.downloadFile' | t: { name: file.name }" [title]="'book.downloadHint' | t: { name: file.name }">
                                        <svg libIcon="download"></svg>
                                    </a>
                                }
                            </li>
                        }
                    </ul>
                    @if (!book.canDownload) {
                        <p class="book-note">{{ 'book.noDownload' | t }}</p>
                    }
                </section>

                <section class="book-section" aria-labelledby="book-details">
                    <h2 id="book-details">{{ 'book.details' | t }}</h2>
                    <dl class="book-details">
                        <div><dt>{{ 'book.added' | t }}</dt><dd>{{ i18n.date(book.added) }}</dd></div>
                        @for (identifier of book.identifiers; track identifier.key + identifier.value) {
                            <div><dt>{{ scheme(identifier.key) }}</dt><dd class="book-identifier">{{ identifier.value }}</dd></div>
                        }
                        @for (property of book.properties ?? []; track property.key) {
                            <div><dt>{{ property.key }}</dt><dd>{{ property.value }}</dd></div>
                        }
                    </dl>
                </section>

                @if (duplicates().length) {
                    <section class="book-section" aria-labelledby="book-duplicates">
                        <h2 id="book-duplicates">{{ 'book.duplicates' | t }}</h2>
                        <p class="book-note">{{ 'book.duplicatesIntro' | t }}</p>
                        <ul class="book-duplicates">
                            @for (duplicate of duplicates(); track duplicate.book.id) {
                                @let other = duplicate.book;
                                <li>
                                    <a class="book-duplicate" [routerLink]="['/books', other.id]">
                                        <span class="book-cover"><img [src]="cover(other.id, 's')" alt="" loading="lazy" decoding="async" width="80" height="120"></span>
                                        <span class="book-text">
                                            <span class="book-title">{{ listTitle(other) }}</span>
                                            <span class="book-authors">{{ other.authors.join(', ') }}</span>
                                            <span class="book-reason">{{ reason(duplicate) }}</span>
                                        </span>
                                        <span class="book-formats">
                                            @for (format of other.formats; track format) {
                                                <span class="badge">{{ format.toUpperCase() }}</span>
                                            }
                                        </span>
                                    </a>
                                </li>
                            }
                        </ul>
                    </section>
                }
            </article>
        } @else if (missing()) {
            <h1>{{ 'book.missingTitle' | t }}</h1>
            <p class="empty large" libEmpty="book">
                {{ 'book.missing' | t }}
                <a routerLink="/">{{ 'tag.backToLibrary' | t }}</a>
            </p>
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }} <button type="button" class="button" (click)="load()">{{ 'common.retry' | t }}</button></p>
        } @else {
            <lib-skeleton [rows]="6" label="book.loading" />
        }
    `,
})
export class BookPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly catalog = inject(FieldCatalog);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly location = inject(Location);
    private readonly libraries = inject(LibraryContext);

    /** Opened from another page of the app (not as the first page loaded): back is the browser's back. */
    private readonly fromApp = this.router.navigated;
    private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
    private readonly id = computed(() => Number(this.params().get('id')));
    protected readonly book = signal<BookDetail | null>(null);
    protected readonly duplicates = signal<BookDuplicate[]>([]);
    protected readonly missing = signal(false);
    protected readonly error = signal<string | null>(null);
    private requested = 0;

    protected readonly authors = computed(() => this.book()?.tags.filter(tag => tag.field === 'author') ?? []);
    protected readonly series = computed(() => this.book()?.tags.filter(tag => tag.field === 'series') ?? []);
    /** The other fields, then the plain tags. */
    protected readonly fields = computed(() => {
        const groups: FieldGroup[] = [];
        for (const tag of this.book()?.tags ?? []) {
            if (tag.field === 'author' || tag.field === 'series')
                continue;
            const group = groups.find(g => g.field === tag.field);
            if (group)
                group.tags.push(tag);
            else
                groups.push({ field: tag.field, tags: [tag] });
        }
        return [...groups.filter(g => g.field !== null), ...groups.filter(g => g.field === null)];
    });
    /** The file the head's Download gives: the first one still there. */
    protected readonly first = computed(() => {
        const book = this.book();
        return book?.canDownload ? book.files.find(file => !file.missing) ?? null : null;
    });
    protected readonly libraryName = computed(() => {
        const book = this.book();
        const libraries = this.libraries.libraries();
        return libraries.length > 1 ? libraries.find(library => library.id === book?.libraryId)?.name ?? null : null;
    });

    constructor() {
        void this.catalog.load();
        void this.libraries.load();
        effect(() => {
            this.id();
            untracked(() => void this.load());
        });
    }

    protected back(): void {
        if (this.fromApp)
            this.location.back();
        else
            void this.router.navigateByUrl('/');
    }

    protected cover(id: number, size: 's' | 'l'): string {
        return coverUrl(id, size);
    }

    protected download(book: BookDetail, file: BookFile): string {
        return fileUrl(book.id, file.id);
    }

    protected title(book: BookDetail): string {
        return book.title || book.fileName || this.i18n.t('browse.untitled');
    }

    protected listTitle(book: BookListItem): string {
        return book.title || book.fileName || this.i18n.t('browse.untitled');
    }

    protected seriesText(tag: BookTag): string {
        return tag.number != null ? this.i18n.t('browse.seriesNumber', { series: tag.name, number: tag.number }) : tag.name;
    }

    /** "Category" for one value, "Categories" for several; "Tags" for the plain ones. */
    protected fieldName(group: FieldGroup): string {
        if (group.field === null)
            return this.i18n.t(group.tags.length > 1 ? 'tags.title' : 'tags.one');
        return group.tags.length > 1 ? this.catalog.title(group.field) : this.catalog.one(group.field);
    }

    /** An identifier's scheme as shown: ISBN, UUID, FB2 id; others (Calibre's `goodreads`…) as they are. */
    protected scheme(key: string): string {
        const text = this.i18n.t(`book.scheme.${key}`);
        return text === `book.scheme.${key}` ? key : text;
    }

    protected reason(duplicate: BookDuplicate): string {
        return duplicate.reason === 'identifier'
            ? this.i18n.t('book.reason.identifier', { scheme: this.scheme(duplicate.scheme ?? '') })
            : this.i18n.t(`book.reason.${duplicate.reason}`);
    }

    protected async load(): Promise<void> {
        const asked = ++this.requested;
        const id = this.id();
        this.book.set(null);
        this.duplicates.set([]);
        this.missing.set(false);
        this.error.set(null);
        if (!Number.isInteger(id) || id <= 0) {
            this.missing.set(true);
            return;
        }
        try {
            const book = await this.api.book(id);
            if (asked !== this.requested)
                return;
            if (book.mergedIntoId) {
                void this.router.navigate(['/books', book.mergedIntoId], { replaceUrl: true });
                return;
            }
            this.book.set(book);
            scrollTo({ top: 0 });
        } catch (error) {
            if (asked !== this.requested)
                return;
            if ((error as { status?: number }).status === 404)
                this.missing.set(true);
            else
                this.error.set(errorText(this.i18n, error));
            return;
        }
        // After the book: the page shows without waiting for them, and a failure only leaves the panel out.
        try {
            const duplicates = await this.api.bookDuplicates(id);
            if (asked === this.requested)
                this.duplicates.set(duplicates);
        } catch {
            // Nothing to suggest.
        }
    }
}
