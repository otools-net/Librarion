import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { Facets } from '../search/facets';
import { BookList } from './book-list';

/**
 * The library (the home page; agents/docs/features/browse.md, D-5.5): the library chosen in the switcher, its name and
 * count over its books (`BookList`), Refine with the library's most frequent values (`Facets`).
 */
@Component({
    selector: 'lib-browse-page',
    imports: [TranslatePipe, EmptyState, Skeleton, RouterLink, BookList, Facets],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'browse-page' },
    template: `
        @if (library(); as library) {
            <lib-book-list [library]="library.id" [label]="'browse.books' | t: { name: library.name }" [setSize]="library.bookCount ?? -1">
                <h1>{{ library.name }}</h1>
                @if (library.bookCount != null) {
                    <p class="browse-count">{{ 'browse.count' | t: { count: i18n.number(library.bookCount) } }}</p>
                }
                <lib-facets listTools [library]="library.id" />
                @if (canManage()) {
                    <a emptyExtra routerLink="/admin/libraries">{{ 'browse.emptyAdmin' | t }}</a>
                }
            </lib-book-list>
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
    protected readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);
    private readonly libraries = inject(LibraryContext);

    protected readonly library = this.libraries.current;
    protected readonly librariesLoaded = signal(false);
    protected readonly canManage = computed(() => this.session.canAny(Rights.manageLibraries, Rights.manageTenants));

    constructor() {
        void this.libraries.load().finally(() => this.librariesLoaded.set(true));
    }
}
