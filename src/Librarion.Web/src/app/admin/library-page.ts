import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { TranslatePipe } from '../core/i18n.service';
import { LibraryInfo } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { groupAnchor } from './admin-sections';
import { LIBRARY_TABS, LibraryPanel, LibraryTab } from './library-panel';

/** The rights that show each tab: the scan right for its runs and the work it starts, the import right too for imports. */
const TAB_RIGHTS: Record<LibraryTab, string[][]> = {
    folders: [],
    runs: [[Rights.scan]],
    import: [[Rights.scan], [Rights.import]],
    missing: [[Rights.scan]],
    schedules: [[Rights.scan]],
};

/**
 * A library of the administration (`admin/libraries/:id/:tab`): breadcrumbs, its name, its sub-pages as tabs (links,
 * one line scrolling sideways on phones), then the tab (`LibraryPanel`).
 */
@Component({
    selector: 'lib-library-page',
    imports: [RouterLink, TranslatePipe, EmptyState, Skeleton, LibraryPanel],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (library(); as library) {
            <header class="admin-head">
                <nav class="admin-crumbs" [attr.aria-label]="'admin.breadcrumb' | t">
                    <ol>
                        <li><a routerLink="/admin" [title]="'admin.overviewHint' | t">{{ 'admin.title' | t }}</a></li>
                        <li class="admin-crumb-group">
                            <a routerLink="/admin" [fragment]="anchor" [title]="'admin.groupHint' | t: { name: ('adminGroup.libraries' | t) }">{{ 'adminGroup.libraries' | t }}</a>
                        </li>
                        <li><a routerLink="/admin/libraries" [title]="'libraries.backToList' | t">{{ 'adminSection.libraries' | t }}</a></li>
                        <li aria-current="page">{{ library.name }}</li>
                    </ol>
                </nav>
                <h1>{{ library.name }}</h1>
                <p>{{ 'libraryTab.' + tab() + '.hint' | t }}</p>
            </header>
            <nav class="library-tabs" [attr.aria-label]="'libraries.pages' | t: { name: library.name }">
                @for (item of tabs(); track item) {
                    <a class="chip" [routerLink]="['/admin/libraries', library.id, item]" replaceUrl
                       [attr.aria-current]="item === tab() ? 'page' : null" [class.active]="item === tab()"
                       [title]="'libraryTab.' + item + '.hint' | t">{{ 'libraryTab.' + item | t }}</a>
                }
            </nav>
            <lib-library-panel [library]="library" [tab]="tab()" />
        } @else if (notFound()) {
            <p class="empty large" libEmpty="library">{{ 'libraries.notFound' | t }}</p>
        } @else {
            <lib-skeleton [rows]="4" />
        }
    `,
})
export class LibraryPage {
    private readonly api = inject(Api);
    private readonly sessions = inject(SessionService);
    private readonly router = inject(Router);
    private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    private readonly injector = inject(Injector);

    readonly libraryId = input.required<number>();
    /** The tab of the address: unknown or not allowed ones show the first. */
    readonly page = input<string | null>(null);

    protected readonly library = signal<LibraryInfo | null>(null);
    protected readonly notFound = signal(false);
    protected readonly tabs = computed(() => {
        this.sessions.session();
        return LIBRARY_TABS.filter(tab => TAB_RIGHTS[tab].every(any => this.sessions.canAny(...any)));
    });
    protected readonly tab = computed<LibraryTab>(() => this.tabs().find(tab => tab === this.page()) ?? 'folders');
    protected readonly anchor = groupAnchor('libraries');

    constructor() {
        effect(() => void this.load(this.libraryId()));
        // The chosen tab in view (phones: the line scrolls sideways).
        effect(() => {
            this.tab();
            afterNextRender(() => this.host.querySelector('.library-tabs .active')?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }), { injector: this.injector });
        });
    }

    private async load(id: number): Promise<void> {
        this.notFound.set(false);
        const libraries = await this.api.libraries().catch(() => []);
        const library = libraries.find(each => each.id === id) ?? null;
        this.library.set(library);
        this.notFound.set(!library);
        if (library && this.page() !== this.tab())
            void this.router.navigate(['/admin/libraries', id, this.tab()], { replaceUrl: true });
    }
}
