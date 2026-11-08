import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, effect, inject, Injector, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountSection } from '../account/account-section';
import { Api } from '../core/api.service';
import { valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { SettingInfo } from '../core/models';
import { SettingReveal } from '../settings/setting-reveal';
import { Media } from '../core/media';
import { UnsavedChanges } from '../core/unsaved-changes';
import { SessionService } from '../core/session.service';
import { Icon } from '../shared/icon';
import { EmptyState } from '../shared/placeholders';
import { ADMIN_SEARCH_LIMIT, AdminSearchResult, searchAdmin, searchWords } from './admin-search';
import { AdminSection, groupAnchor, groupsOf, movedPage, visibleSections } from './admin-sections';
import { MarkedText } from './marked-text';
import { GroupsSection } from './groups-section';
import { LibrariesSection } from './libraries-section';
import { LibraryPage } from './library-page';
import { SecuritySection } from './security-section';
import { SettingsSection } from './settings-section';
import { SystemSection } from './system-section';
import { TagsSection } from './tags-section';
import { ImportedTagsSection } from './imported-tags-section';
import { CoverStylesSection } from './cover-styles-section';
import { DuplicatesSection } from './duplicates-section';
import { TasksSection } from './tasks-section';
import { TenantsSection } from './tenants-section';
import { UsersSection } from './users-section';

/**
 * The administration (port of SFE's AdminPageComponent; agents/docs/features/admin-page.md): its own left column of
 * groups and sections (the open section's sub-pages under it), breadcrumbs, the sub-page's title and description, then
 * the sub-page. `/admin` alone is the overview, cards by group, unless the session sees a single section. Phones: `/admin`
 * is the list of sections (description and chevron each), a section a page of its own with its sub-pages as chips;
 * back (the top bar's arrow or the browser's) returns to the list. A search box above the sections finds sections,
 * sub-pages and settings (admin-search.ts); a setting picked is brought into view by its panel (SettingReveal).
 */
@Component({
    selector: 'lib-admin-page',
    imports: [RouterLink, TranslatePipe, Icon, EmptyState, MarkedText, AccountSection, UsersSection, GroupsSection, LibrariesSection, TenantsSection,
        SettingsSection, SecuritySection, SystemSection, LibraryPage, TasksSection, TagsSection, ImportedTagsSection, CoverStylesSection, DuplicatesSection],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'admin-page' },
    template: `
        @if (sections().length === 0) {
            <p class="empty large" libEmpty="shield">{{ 'admin.nothing' | t }}</p>
        } @else {
            <div class="admin-layout" [class.section-list]="sectionList()">
                @if (sectionList()) {
                    <header class="admin-head">
                        <h1>{{ 'admin.title' | t }}</h1>
                        <p>{{ 'admin.overview' | t }}</p>
                    </header>
                }
                @if (!phone() || sectionList()) {
                    <nav class="admin-nav" [attr.aria-label]="'admin.sections' | t">
                        <div class="admin-search" role="search">
                            <svg libIcon="search"></svg>
                            <input #searchBox type="search" [placeholder]="'adminSearch.placeholder' | t" [attr.aria-label]="'adminSearch.label' | t"
                                   [title]="'adminSearch.hint' | t" [attr.aria-controls]="searching() ? 'admin-search-results' : null"
                                   spellcheck="false" autocomplete="off" enterkeyhint="go" [value]="query()"
                                   (focus)="loadCatalog()" (input)="onQuery($event)" (keydown)="onSearchKey($event)">
                            @if (query()) {
                                <button type="button" class="icon-button admin-search-clear" [attr.aria-label]="'adminSearch.clear' | t"
                                        [title]="'adminSearch.clearHint' | t" (click)="query.set(''); searchBox.focus()">
                                    <svg libIcon="close"></svg>
                                </button>
                            }
                        </div>
                        @if (searching()) {
                            <p class="admin-search-count" role="status">{{ resultsCount() }}</p>
                            <ul class="admin-search-results" id="admin-search-results" [attr.aria-label]="'adminSearch.results' | t">
                                @for (result of shownResults(); track $index) {
                                    <li>
                                        <a [routerLink]="link(result)" [replaceUrl]="!phone()" [title]="'admin.open' | t: { name: result.where }" (click)="found(result)">
                                            <span class="admin-search-title"><lib-marked-text [text]="result.title" [words]="words()" /></span>
                                            <small class="admin-search-where">{{ result.where }}</small>
                                            @if (result.text) {
                                                <small class="admin-search-text"><lib-marked-text [text]="result.text" [words]="words()" /></small>
                                            }
                                        </a>
                                    </li>
                                }
                            </ul>
                        }
                        @for (group of searching() ? [] : groups(); track group.id) {
                            <p class="group-label">{{ 'adminGroup.' + group.id | t }}</p>
                            <ul>
                                @for (item of group.sections; track item.id) {
                                    @let open = !overview() && item.id === section()?.id;
                                    <li>
                                        <a class="admin-nav-item" [routerLink]="['/admin', item.id]" [replaceUrl]="!phone()" [class.open]="open"
                                           [attr.aria-current]="open && item.subpages.length === 1 ? 'page' : null" [title]="'adminSection.' + item.id + '.hint' | t">
                                            <svg [libIcon]="item.icon"></svg>
                                            <span class="admin-nav-title">{{ 'adminSection.' + item.id | t }}</span>
                                            @if (open && unsaved.dirty()) {
                                                <span class="admin-mark" [title]="'admin.unsavedMark' | t"><span class="visually-hidden">{{ 'admin.unsavedMark' | t }}</span></span>
                                            }
                                            @if (phone()) {
                                                <small class="admin-nav-description">{{ 'adminSection.' + item.id + '.hint' | t }}</small>
                                                <svg libIcon="chevron-right" class="admin-nav-chevron"></svg>
                                            }
                                        </a>
                                        @if (open && item.subpages.length > 1) {
                                            <ul class="admin-subnav" [attr.aria-label]="'adminSection.' + item.id | t">
                                                @for (sub of item.subpages; track sub.id) {
                                                    <li>
                                                        <a [routerLink]="['/admin', item.id, sub.id]" replaceUrl [attr.aria-current]="sub.id === subpage()?.id ? 'page' : null"
                                                           [title]="'adminPage.' + item.id + '.' + sub.id + '.hint' | t">
                                                        {{ 'adminPage.' + item.id + '.' + sub.id | t }}
                                                        @if (sub.id === subpage()?.id && unsaved.dirty()) {
                                                            <span class="admin-mark" [title]="'admin.unsavedMark' | t"><span class="visually-hidden">{{ 'admin.unsavedMark' | t }}</span></span>
                                                        }
                                                    </a>
                                                    </li>
                                                }
                                            </ul>
                                        }
                                    </li>
                                }
                            </ul>
                        }
                    </nav>
                }

                <div class="admin-main">
                    @if (overview() && !sectionList()) {
                        <header class="admin-head">
                            <h1>{{ 'admin.title' | t }}</h1>
                            <p>{{ 'admin.overview' | t }}</p>
                        </header>
                        @for (group of groups(); track group.id) {
                            <section class="admin-overview-group" [attr.aria-labelledby]="anchor(group.id)">
                                <h2 class="group-label" [id]="anchor(group.id)">{{ 'adminGroup.' + group.id | t }}</h2>
                                <div class="admin-cards">
                                    @for (item of group.sections; track item.id) {
                                        <article class="card admin-card">
                                            <a class="card-title" [routerLink]="['/admin', item.id]" [title]="'admin.open' | t: { name: ('adminSection.' + item.id | t) }">
                                                <svg [libIcon]="item.icon"></svg>
                                                <span>{{ 'adminSection.' + item.id | t }}</span>
                                            </a>
                                            <p>{{ 'adminSection.' + item.id + '.hint' | t }}</p>
                                            @if (item.subpages.length > 1) {
                                                <ul class="admin-card-pages" [attr.aria-label]="'adminSection.' + item.id | t">
                                                    @for (sub of item.subpages; track sub.id) {
                                                        <li>
                                                            <a class="chip" [routerLink]="['/admin', item.id, sub.id]"
                                                               [title]="'adminPage.' + item.id + '.' + sub.id + '.hint' | t">{{ 'adminPage.' + item.id + '.' + sub.id | t }}</a>
                                                        </li>
                                                    }
                                                </ul>
                                            }
                                        </article>
                                    }
                                </div>
                            </section>
                        }
                    } @else if (library(); as id) {
                        <lib-library-page [libraryId]="id" [page]="params().get('page')" />
                    } @else if (section(); as section) {
                        @let many = section.subpages.length > 1;
                        <header class="admin-head">
                            <nav class="admin-crumbs" [attr.aria-label]="'admin.breadcrumb' | t">
                                <ol>
                                    <li><a routerLink="/admin" [title]="'admin.overviewHint' | t">{{ 'admin.title' | t }}</a></li>
                                    <li class="admin-crumb-group">
                                        <a routerLink="/admin" [fragment]="anchor(section.group)" [title]="'admin.groupHint' | t: { name: ('adminGroup.' + section.group | t) }">{{ 'adminGroup.' + section.group | t }}</a>
                                    </li>
                                    @if (many) {
                                        <li><a [routerLink]="['/admin', section.id]" [title]="'admin.open' | t: { name: ('adminSection.' + section.id | t) }">{{ 'adminSection.' + section.id | t }}</a></li>
                                        <li aria-current="page">{{ 'adminPage.' + section.id + '.' + subpage()!.id | t }}</li>
                                    } @else {
                                        <li aria-current="page">{{ 'adminSection.' + section.id | t }}</li>
                                    }
                                </ol>
                            </nav>
                            <h1>{{ (many ? 'adminPage.' + section.id + '.' + subpage()!.id : 'adminSection.' + section.id) | t }}</h1>
                            <p>{{ (many ? 'adminPage.' + section.id + '.' + subpage()!.id + '.hint' : 'adminSection.' + section.id + '.hint') | t }}</p>
                        </header>
                        @if (phone() && many) {
                            <div class="admin-chips" role="tablist" [attr.aria-label]="'adminSection.' + section.id | t" (keydown)="chipKey($event, section)">
                                @for (sub of section.subpages; track sub.id) {
                                    <button type="button" role="tab" class="chip" [id]="'admin-chip-' + sub.id" aria-controls="admin-panel"
                                            [attr.aria-selected]="sub.id === subpage()!.id" [tabIndex]="sub.id === subpage()!.id ? 0 : -1"
                                            [title]="'adminPage.' + section.id + '.' + sub.id + '.hint' | t" (click)="choose(section, sub.id)">
                                        {{ 'adminPage.' + section.id + '.' + sub.id | t }}
                                    </button>
                                }
                            </div>
                        }
                        <div id="admin-panel" class="admin-panel" [attr.role]="phone() && many ? 'tabpanel' : null"
                             [attr.aria-labelledby]="phone() && many ? 'admin-chip-' + subpage()!.id : null">
                            @switch (section.id) {
                                @case ('account') { <lib-account-section [page]="subpage()!.id" /> }
                                @case ('libraries') { <lib-libraries-section /> }
                                @case ('librarySettings') { <lib-settings-section level="library" /> }
                                @case ('tags') { <lib-tags-section [page]="subpage()!.id" /> }
                                @case ('importedTags') { <lib-imported-tags-section [page]="subpage()!.id" /> }
                                @case ('coverStyles') { <lib-cover-styles-section /> }
                                @case ('duplicates') { <lib-duplicates-section /> }
                                @case ('users') { <lib-users-section /> }
                                @case ('groups') { <lib-groups-section /> }
                                @case ('tasks') { <lib-tasks-section [page]="subpage()!.id" /> }
                                @case ('tenants') { <lib-tenants-section /> }
                                @case ('settings') { <lib-settings-section [level]="subpage()!.id === 'server' ? 'server' : 'tenant'" /> }
                                @case ('security') { <lib-security-section /> }
                                @case ('system') { <lib-system-section /> }
                            }
                        </div>
                    }
                </div>
            </div>
        }
    `,
})
export class AdminPage {
    private readonly sessions = inject(SessionService);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);
    private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    private readonly injector = inject(Injector);
    protected readonly phone = inject(Media).phone;
    protected readonly unsaved = inject(UnsavedChanges);
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly reveal = inject(SettingReveal);

    protected readonly params = toSignal(this.route.paramMap, { requireSync: true });
    private readonly fragment = toSignal(this.route.fragment);

    protected readonly sections = computed(() => {
        this.sessions.session();
        return visibleSections((...rights) => this.sessions.canAny(...rights));
    });
    protected readonly groups = computed(() => groupsOf(this.sections()));
    protected readonly section = computed<AdminSection | undefined>(() => this.sections().find(s => s.id === this.params().get('section')));
    protected readonly subpage = computed(() => {
        const section = this.section();
        return section?.subpages.find(page => page.id === this.params().get('page')) ?? section?.subpages[0];
    });
    protected readonly overview = computed(() => !this.params().get('section') && this.sections().length > 1);
    /** A library's own pages, in the Libraries section. */
    protected readonly library = computed(() => this.section()?.id === 'libraries' ? Number(this.params().get('library')) || null : null);
    /** Phones: the overview is the list of sections. */
    protected readonly sectionList = computed(() => this.phone() && this.overview());

    protected readonly anchor = groupAnchor;

    /** The search box's text, and what it finds among the sections the session sees. */
    protected readonly query = signal('');
    protected readonly words = computed(() => searchWords(this.query()));
    protected readonly searching = computed(() => this.words().length > 0);
    /** The settings catalog, read when the search box is first used (none when the session sees no settings page). */
    private readonly catalog = signal<SettingInfo[]>([]);
    private catalogAsked = false;
    private readonly results = computed(() => searchAdmin(this.query(), this.sections(), this.catalog(), key => this.i18n.t(key)));
    protected readonly shownResults = computed(() => this.results().slice(0, ADMIN_SEARCH_LIMIT));
    protected readonly resultsCount = computed(() => {
        const count = this.results().length;
        return count === 0 ? this.i18n.t('adminSearch.none')
            : count > ADMIN_SEARCH_LIMIT ? this.i18n.t('adminSearch.more', { limit: ADMIN_SEARCH_LIMIT, count })
                : count === 1 ? this.i18n.t('adminSearch.one') : this.i18n.t('adminSearch.count', { count });
    });

    constructor() {
        // Moved, unknown or not allowed addresses; a session seeing one section gets it instead of the overview.
        effect(() => {
            const params = this.params();
            const section = params.get('section');
            const moved = movedPage(section, params.get('page'));
            const sections = this.sections();
            if (moved)
                void this.router.navigate(['/admin', moved.section, moved.page], { replaceUrl: true });
            else if (section && !this.section())
                void this.router.navigate(['/admin'], { replaceUrl: true });
            else if (!section && sections.length === 1)
                void this.router.navigate(['/admin', sections[0].id], { replaceUrl: true });
        });
        // Phones: the chosen chip in view (its line scrolls sideways; SFE's chip-in-view rule).
        effect(() => {
            const page = this.subpage()?.id;
            if (page && this.phone())
                afterNextRender(() => this.host.querySelector('.admin-chips [aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }),
                    { injector: this.injector });
        });
        // The breadcrumbs' group: its heading on the overview (no router anchor scrolling).
        effect(() => {
            const fragment = this.fragment();
            if (fragment && this.overview())
                afterNextRender(() => this.host.querySelector(`#${CSS.escape(fragment)}`)?.scrollIntoView?.({ block: 'start' }), { injector: this.injector });
        });
    }

    /** Reads the settings catalog once, if a settings page is among the session's. */
    protected loadCatalog(): void {
        if (this.catalogAsked || !this.sections().some(section => section.subpages.some(page => page.settings)))
            return;
        this.catalogAsked = true;
        this.api.settingCatalog().then(catalog => this.catalog.set(catalog), () => this.catalogAsked = false);
    }

    protected onQuery(event: Event): void {
        this.loadCatalog();
        this.query.set(valueOf(event));
    }

    protected link(result: AdminSearchResult): string[] {
        return result.subpage.id === result.section.subpages[0].id ? ['/admin', result.section.id] : ['/admin', result.section.id, result.subpage.id];
    }

    /** A result is opened: a setting is brought into view once its sub-page shows it. */
    protected found(result: AdminSearchResult): void {
        if (result.setting && result.subpage.settings)
            this.reveal.request(result.setting, result.subpage.settings);
    }

    /** Enter opens the first result, Escape empties the box. */
    protected onSearchKey(event: KeyboardEvent): void {
        if (event.key === 'Escape' && this.query()) {
            event.preventDefault();
            this.query.set('');
        } else if (event.key === 'Enter') {
            event.preventDefault();
            const first = this.results()[0];
            if (!first)
                return;
            this.found(first);
            void this.router.navigate(this.link(first), { replaceUrl: !this.phone() });
        }
    }

    protected choose(section: AdminSection, page: string): void {
        void this.router.navigate(['/admin', section.id, page], { replaceUrl: true });
    }

    /** The chips are tabs: arrows, Home and End move between them (and open the one reached). */
    protected chipKey(event: KeyboardEvent, section: AdminSection): void {
        const pages = section.subpages.map(page => page.id);
        const at = pages.indexOf(this.subpage()!.id);
        const next = { ArrowRight: at + 1, ArrowLeft: at - 1, Home: 0, End: pages.length - 1 }[event.key];
        if (next === undefined)
            return;
        event.preventDefault();
        const page = pages[(next + pages.length) % pages.length];
        this.choose(section, page);
        afterNextRender(() => this.host.querySelector<HTMLElement>(`#admin-chip-${page}`)?.focus(), { injector: this.injector });
    }
}
