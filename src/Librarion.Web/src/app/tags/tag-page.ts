import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { BookSort, TagDetail, TagRef } from '../core/models';
import { BookList } from '../browse/book-list';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { FieldCatalog } from './field-catalog';

const SERIES_SORTS: readonly BookSort[] = ['number', 'title', 'author', 'added'];

/**
 * A tag or field value (`tags/:id`; agents/docs/features/browse.md): what it is (its field, linking to the field's
 * list), its name and books in the library being browsed, the tags it is under and those under it, then its books
 * (`BookList` with the tag; a series by its numbers first).
 */
@Component({
    selector: 'lib-tag-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink, BookList],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'tag-page' },
    template: `
        @if (tag(); as tag) {
            @if (library(); as library) {
                <lib-book-list [library]="library.id" [tag]="tag.id" [label]="'tag.books' | t: { name: name(tag) }"
                               [setSize]="tag.bookCount ?? -1" emptyText="tag.empty"
                               [sorts]="series() ? seriesSorts : defaultSorts" [sortScope]="series() ? 'series' : 'library'">
                    <a class="tag-kind" [routerLink]="tag.field ? ['/fields', tag.field] : '/tags'">
                        <svg [libIcon]="catalog.icon(tag.field)"></svg>{{ tag.field ? catalog.one(tag.field, tag.fieldName) : ('tags.one' | t) }}
                    </a>
                    <h1>{{ name(tag) }}</h1>
                    @if (tag.bookCount != null) {
                        <p class="browse-count">{{ 'tag.count' | t: { count: i18n.number(tag.bookCount), library: library.name } }}</p>
                    }
                    @if (tag.parents.length) {
                        <p class="tag-related">
                            <span class="tag-related-label">{{ 'tag.under' | t }}</span>
                            @for (parent of tag.parents; track parent.id) {
                                <a class="chip" [routerLink]="['/tags', parent.id]">{{ refName(parent) }}</a>
                            }
                        </p>
                    }
                    @if (tag.children.length) {
                        <p class="tag-related">
                            <span class="tag-related-label">{{ 'tag.narrower' | t }}</span>
                            @for (child of tag.children; track child.id) {
                                <a class="chip" [routerLink]="['/tags', child.id]">{{ refName(child) }}</a>
                            }
                            @if (tag.moreChildren) {
                                <span class="tag-related-more">{{ 'tag.moreNarrower' | t }}</span>
                            }
                        </p>
                    }
                </lib-book-list>
            } @else if (librariesLoaded()) {
                <h1>{{ name(tag) }}</h1>
                <p class="empty large" libEmpty="library">{{ 'browse.noLibrary' | t }}</p>
            } @else {
                <lib-skeleton [rows]="6" label="tag.loading" />
            }
        } @else if (missing()) {
            <h1>{{ 'tag.missingTitle' | t }}</h1>
            <p class="empty large" libEmpty="tag">
                {{ 'tag.missing' | t }}
                <a routerLink="/">{{ 'tag.backToLibrary' | t }}</a>
            </p>
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }} <button type="button" class="button" (click)="load()">{{ 'common.retry' | t }}</button></p>
        } @else {
            <lib-skeleton [rows]="6" label="tag.loading" />
        }
    `,
})
export class TagPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly catalog = inject(FieldCatalog);
    private readonly route = inject(ActivatedRoute);
    private readonly libraries = inject(LibraryContext);

    protected readonly seriesSorts = SERIES_SORTS;
    protected readonly defaultSorts: readonly BookSort[] = ['title', 'author', 'added'];
    private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
    private readonly id = computed(() => Number(this.params().get('id')));
    protected readonly library = this.libraries.current;
    protected readonly librariesLoaded = signal(false);
    protected readonly tag = signal<TagDetail | null>(null);
    protected readonly missing = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly series = computed(() => this.tag()?.field === 'series');
    private requested = 0;

    constructor() {
        void this.catalog.load();
        void this.libraries.load().finally(() => this.librariesLoaded.set(true));
        // Another tag, or the counts of another library.
        effect(() => {
            this.id();
            this.library()?.id;
            untracked(() => void this.load());
        });
    }

    protected name(tag: TagDetail): string {
        return this.catalog.value(tag.field, tag.name);
    }

    protected refName(tag: TagRef): string {
        return this.catalog.value(tag.field, tag.name);
    }

    protected async load(): Promise<void> {
        const asked = ++this.requested;
        const id = this.id();
        // The same tag (another library's counts) stays shown while asked again.
        if (this.tag()?.id !== id)
            this.tag.set(null);
        this.missing.set(false);
        this.error.set(null);
        if (!Number.isInteger(id) || id <= 0) {
            this.missing.set(true);
            return;
        }
        try {
            const tag = await this.api.tag(id, this.library()?.id);
            if (asked === this.requested)
                this.tag.set(tag);
        } catch (error) {
            if (asked !== this.requested)
                return;
            if ((error as { status?: number }).status === 404)
                this.missing.set(true);
            else
                this.error.set(errorText(this.i18n, error));
        }
    }
}
