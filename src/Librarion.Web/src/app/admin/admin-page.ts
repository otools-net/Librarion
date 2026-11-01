import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, effect, inject, Injector } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, UrlMatchResult, UrlSegment } from '@angular/router';
import { AccountSection } from '../account/account-section';
import { TranslatePipe } from '../core/i18n.service';
import { Media } from '../core/media';
import { SessionService } from '../core/session.service';
import { Icon } from '../shared/icon';
import { EmptyState } from '../shared/placeholders';
import { AdminSection, groupAnchor, groupsOf, movedPage, visibleSections } from './admin-sections';
import { GroupsSection } from './groups-section';
import { LibrariesSection } from './libraries-section';
import { SecuritySection } from './security-section';
import { SettingsSection } from './settings-section';
import { SystemSection } from './system-section';
import { TenantsSection } from './tenants-section';
import { UsersSection } from './users-section';

/** `admin`, `admin/:section`, `admin/:section/:page`: one route, so the page (and its column) stays between sections. */
export function adminMatcher(segments: UrlSegment[]): UrlMatchResult | null {
    if (segments[0]?.path !== 'admin' || segments.length > 3)
        return null;
    const posParams: Record<string, UrlSegment> = {};
    if (segments[1])
        posParams['section'] = segments[1];
    if (segments[2])
        posParams['page'] = segments[2];
    return { consumed: segments, posParams };
}

/**
 * The administration (port of SFE's AdminPageComponent; agents/docs/features/admin-page.md): its own left column of
 * groups and sections (the open section's sub-pages under it), breadcrumbs, the sub-page's title and description, then
 * the sub-page. `/admin` alone is the overview, cards by group, unless the session sees a single section. Phones: `/admin`
 * is the list of sections (description and chevron each), a section a page of its own with its sub-pages as chips;
 * back (the top bar's arrow or the browser's) returns to the list.
 */
@Component({
    selector: 'lib-admin-page',
    imports: [RouterLink, TranslatePipe, Icon, EmptyState, AccountSection, UsersSection, GroupsSection, LibrariesSection, TenantsSection,
        SettingsSection, SecuritySection, SystemSection],
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
                        @for (group of groups(); track group.id) {
                            <p class="group-label">{{ 'adminGroup.' + group.id | t }}</p>
                            <ul>
                                @for (item of group.sections; track item.id) {
                                    @let open = !overview() && item.id === section()?.id;
                                    <li>
                                        <a class="admin-nav-item" [routerLink]="['/admin', item.id]" [replaceUrl]="!phone()" [class.open]="open"
                                           [attr.aria-current]="open && item.subpages.length === 1 ? 'page' : null" [title]="'adminSection.' + item.id + '.hint' | t">
                                            <svg [libIcon]="item.icon"></svg>
                                            <span class="admin-nav-title">{{ 'adminSection.' + item.id | t }}</span>
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
                                                           [title]="'adminPage.' + item.id + '.' + sub.id + '.hint' | t">{{ 'adminPage.' + item.id + '.' + sub.id | t }}</a>
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
                                @case ('users') { <lib-users-section /> }
                                @case ('groups') { <lib-groups-section /> }
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

    private readonly params = toSignal(this.route.paramMap, { requireSync: true });
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
    /** Phones: the overview is the list of sections. */
    protected readonly sectionList = computed(() => this.phone() && this.overview());

    protected readonly anchor = groupAnchor;

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
