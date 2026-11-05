import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { BookList } from '../browse/book-list';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { Facets } from './facets';

/** The language by example: each opens its search (the texts say what it finds). */
const EXAMPLES: readonly { query: string; text: string }[] = [
    { query: 'tolkien hobbit', text: 'search.help.words' },
    { query: '"the dark tower"', text: 'search.help.phrase' },
    { query: 'hob*', text: 'search.help.prefix' },
    { query: 'author:"Stephen King"', text: 'search.help.field' },
    { query: 'series:', text: 'search.help.anyValue' },
    { query: 'category:Fantasy -author:Pratchett', text: 'search.help.not' },
    { query: 'format:epub', text: 'search.help.format' },
    { query: 'tag:Favourites', text: 'search.help.tag' },
];

/**
 * The results of the top bar's search (`search?q=`; agents/docs/features/search.md): the books of the library being
 * browsed that match, as the library's list (grid or list, sort, pages by key, the way back kept), how many (from the
 * facets' count: "2,000+" past their cap), Refine (`Facets`), and how to search.
 */
@Component({
    selector: 'lib-search-page',
    imports: [TranslatePipe, RouterLink, BookList, EmptyState, Skeleton, Facets],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'search-page' },
    template: `
        @if (library(); as library) {
            @if (query()) {
                <lib-book-list [library]="library.id" [query]="query()" [label]="'search.books' | t: { query: query() }" emptyText="search.empty">
                    <h1>{{ 'search.title' | t }}</h1>
                    <p class="browse-count">{{ 'search.in' | t: { query: query(), library: library.name } }}@if (count(); as count) {<span class="search-count"> · {{ count }}</span>}</p>
                    <lib-facets listTools [library]="library.id" [query]="query()" />
                </lib-book-list>
            } @else {
                <h1>{{ 'search.title' | t }}</h1>
                <p class="browse-count">{{ 'search.start' | t }}</p>
            }
            <details class="search-help" [open]="!query()">
                <summary>{{ 'search.help.title' | t }}</summary>
                <dl>
                    @for (example of examples; track example.query) {
                        <div>
                            <dt><a [routerLink]="['/search']" [queryParams]="{ q: example.query }"><code>{{ example.query }}</code></a></dt>
                            <dd>{{ example.text | t }}</dd>
                        </div>
                    }
                </dl>
            </details>
        } @else if (librariesLoaded()) {
            <h1>{{ 'search.title' | t }}</h1>
            <p class="empty large" libEmpty="library">{{ 'browse.noLibrary' | t }}</p>
        } @else {
            <lib-skeleton [rows]="6" label="browse.loading" />
        }
    `,
})
export class SearchPage {
    private readonly libraries = inject(LibraryContext);
    private readonly params = toSignal(inject(ActivatedRoute).queryParamMap);

    protected readonly examples = EXAMPLES;
    protected readonly query = computed(() => this.params()?.get('q')?.trim() ?? '');
    protected readonly library = this.libraries.current;
    protected readonly librariesLoaded = signal(false);
    private readonly i18n = inject(I18n);
    private readonly facets = viewChild(Facets);
    protected readonly count = computed(() => {
        const list = this.facets()?.list();
        if (!list)
            return null;
        const count = this.i18n.number(list.counted);
        return this.i18n.t(list.capped ? 'search.countCapped' : 'search.count', { count });
    });

    constructor() {
        void this.libraries.load().finally(() => this.librariesLoaded.set(true));
    }
}
