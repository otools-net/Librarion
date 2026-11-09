import { Location } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, coverUrl, fileUrl } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { ToastService } from '../core/toast.service';
import { BookDetail, BookDuplicate, BookFile, BookListItem, BookTag } from '../core/models';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { FieldCatalog } from '../tags/field-catalog';
import { BookEditor } from './book-editor';
import { CoverEditor } from './cover-editor';
import { BookCompare } from './book-compare';
import { tagLink } from '../tags/tag-link';

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
 * book it now lives in. Edit (with `library.edit` or `library.tag` in its library) opens `BookEditor`.
 */
@Component({
    selector: 'lib-book-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink, BookEditor, CoverEditor, BookCompare],
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
                        <img [src]="cover(book.id, 'l', book.coverVersion)" alt="" width="600" height="900" decoding="async">
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
                                    <a [routerLink]="link(author)">{{ author.name }}</a>@if (!last) {, }
                                }
                            </p>
                        }
                        @for (series of series(); track series.id) {
                            <p class="book-in-series">
                                <svg libIcon="series"></svg>
                                <a [routerLink]="link(series)">{{ seriesText(series) }}</a>
                            </p>
                        }
                        @if (book.rating) {
                            <p class="book-rating" [attr.aria-label]="'book.ratingLabel' | t: { stars: i18n.number(book.rating / 2) }">
                                <svg libIcon="star"></svg>{{ 'book.rating' | t: { stars: i18n.number(book.rating / 2) } }}
                            </p>
                        }
                        @if (first() || book.canEdit || book.canTag) {
                            <p class="book-actions">
                                @if (first(); as file) {
                                    <a class="button primary" [href]="download(book, file)" download [title]="'book.downloadHint' | t: { name: file.name }">
                                        <svg libIcon="download"></svg>{{ 'book.download' | t: { format: file.format.toUpperCase() } }}
                                    </a>
                                }
                                @if (book.canEdit || book.canTag) {
                                    <button type="button" class="button" [title]="'book.editHint' | t" (click)="editing.set(true)">
                                        <svg libIcon="edit"></svg>{{ 'book.edit' | t }}
                                    </button>
                                }
                                @if (book.canEdit) {
                                    <button type="button" class="button" [title]="'cover.editHint' | t" (click)="coverEditing.set(true)">
                                        <svg libIcon="book"></svg>{{ 'cover.edit' | t }}
                                    </button>
                                }
                            </p>
                        }
                        @if (fields().length) {
                            <dl class="book-fields">
                                @for (group of fields(); track group.field) {
                                    <div class="book-field">
                                        <dt>{{ fieldName(group) }}</dt>
                                        <dd>
                                            @for (tag of group.tags; track tag.id) {
                                                <a class="chip" [routerLink]="link(tag)">{{ catalog.value(tag.field, tag.name) }}</a>
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
                    <div class="book-section-head">
                        <h2 id="book-files">{{ 'book.files' | t }}</h2>
                        @if (canSplit() && !splitting()) {
                            <button type="button" class="button" [title]="'book.splitHint' | t" (click)="startSplit()">{{ 'book.split' | t }}</button>
                        }
                    </div>
                    @if (splitting()) {
                        <p class="field-hint" id="book-split-hint">{{ 'book.splitIntro' | t }}</p>
                    }
                    <ul class="book-files">
                        @for (file of book.files; track file.id) {
                            <li class="book-file" [class.missing]="file.missing">
                                @if (splitting()) {
                                    <input type="checkbox" class="book-file-check" [checked]="splitFiles().has(file.id)" (change)="chooseFile(file)"
                                           [attr.aria-label]="'book.splitFile' | t: { name: file.name }" aria-describedby="book-split-hint">
                                }
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
                    @if (splitting()) {
                        <div class="book-split-actions">
                            <button type="button" class="button primary" [disabled]="busy() || !splitFiles().size || splitFiles().size === book.files.length"
                                    (click)="split(book)">{{ 'book.splitAction' + (splitFiles().size === 1 ? '.one' : '') | t: { count: i18n.number(splitFiles().size) } }}</button>
                            <button type="button" class="button" [disabled]="busy()" (click)="splitting.set(false)">{{ 'common.cancel' | t }}</button>
                        </div>
                    }
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
                                        <span class="book-cover"><img [src]="cover(other.id, 's', other.coverVersion)" alt="" loading="lazy" decoding="async" width="80" height="120"></span>
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
                                    @if (book.canMerge) {
                                        <div class="book-duplicate-actions">
                                            <button type="button" class="button" [title]="'compare.openHint' | t" (click)="comparing.set([book.id, other.id])">{{ 'compare.open' | t }}</button>
                                            <button type="button" class="button" [title]="'compare.notHint' | t" [disabled]="busy()" (click)="notDuplicate(other)">{{ 'compare.not' | t }}</button>
                                        </div>
                                    }
                                </li>
                            }
                        </ul>
                    </section>
                }
            </article>
            @if (editing()) {
                <lib-book-editor [book]="book" (saved)="edited($event)" (closed)="editing.set(false)" />
            }
            @if (comparing(); as ids) {
                <lib-book-compare [bookIds]="ids" (merged)="merged($event)" (closed)="comparing.set(null)" />
            }
            @if (coverEditing()) {
                <lib-cover-editor [book]="book" (changed)="book$.set($event)" (closed)="coverEditing.set(false)" />
            }
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
    private readonly toasts = inject(ToastService);
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
    protected readonly editing = signal(false);
    protected readonly coverEditing = signal(false);
    /** The books compared (this one and a possible duplicate), while Compare and merge is open. */
    protected readonly comparing = signal<number[] | null>(null);
    protected readonly busy = signal(false);
    /** Split: files chosen to move out into a new book (stage 6 chunk 13). */
    protected readonly splitting = signal(false);
    protected readonly splitFiles = signal<ReadonlySet<number>>(new Set());
    /** Split is offered with the merge right on a book of two files or more. */
    protected readonly canSplit = computed(() => !!this.book()?.canMerge && (this.book()?.files.length ?? 0) >= 2);
    protected readonly book$ = this.book;
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

    /** The book as saved; its possible duplicates asked again (a title or first author may have changed). */
    protected edited(book: BookDetail): void {
        this.book.set(book);
        void this.api.bookDuplicates(book.id).then(duplicates => this.book()?.id === book.id && this.duplicates.set(duplicates), () => { });
    }

    /** Merged: this page shows the kept book (another one's when this one was merged into it). */
    protected merged(book: BookDetail): void {
        if (book.id !== this.book()?.id)
            void this.router.navigate(['/books', book.id], { replaceUrl: true });
        else
            this.edited(book);
    }

    protected startSplit(): void {
        this.splitFiles.set(new Set());
        this.splitting.set(true);
    }

    protected chooseFile(file: BookFile): void {
        this.splitFiles.update(chosen => {
            const next = new Set(chosen);
            if (!next.delete(file.id))
                next.add(file.id);
            return next;
        });
    }

    /** The chosen files moved out: the new book's page opens (this one is a step back). */
    protected async split(book: BookDetail): Promise<void> {
        this.busy.set(true);
        try {
            const created = await this.api.splitBook(book.id, [...this.splitFiles()]);
            this.splitting.set(false);
            this.toasts.show(this.i18n.t('book.splitDone', { title: created ? created.title || created.fileName || '' : '' }));
            if (created)
                void this.router.navigate(['/books', created.id]);
            else
                await this.load();
        } catch (error) {
            this.toasts.show(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async notDuplicate(other: BookListItem): Promise<void> {
        const book = this.book();
        if (!book)
            return;
        this.busy.set(true);
        try {
            await this.api.notDuplicates([book.id, other.id]);
            this.duplicates.update(list => list.filter(d => d.book.id !== other.id));
        } catch (error) {
            this.toasts.show(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected cover(id: number, size: 's' | 'l', version?: string | null): string {
        return coverUrl(id, size, version);
    }

    protected download(book: BookDetail, file: BookFile): string {
        return fileUrl(book.id, file.id);
    }

    protected link(tag: BookTag): (string | number)[] {
        return tagLink(tag);
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
