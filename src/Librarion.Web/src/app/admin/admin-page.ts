import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, effect, inject, Injector } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, UrlMatchResult, UrlSegment } from '@angular/router';
import { AccountSection } from '../account/account-section';
import { TranslatePipe } from '../core/i18n.service';
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
 * the sub-page. `/admin` alone is the overview, cards by group, unless the session sees a single section.
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
            <div class="admin-layout">
                <nav class="admin-nav" [attr.aria-label]="'admin.sections' | t">
                    @for (group of groups(); track group.id) {
                        <p class="group-label">{{ 'adminGroup.' + group.id | t }}</p>
                        <ul>
                            @for (item of group.sections; track item.id) {
                                @let open = !overview() && item.id === section()?.id;
                                <li>
                                    <a class="admin-nav-item" [routerLink]="['/admin', item.id]" [class.open]="open"
                                       [attr.aria-current]="open && item.subpages.length === 1 ? 'page' : null" [title]="'adminSection.' + item.id + '.hint' | t">
                                        <svg [libIcon]="item.icon"></svg>
                                        <span>{{ 'adminSection.' + item.id | t }}</span>
                                    </a>
                                    @if (open && item.subpages.length > 1) {
                                        <ul class="admin-subnav" [attr.aria-label]="'adminSection.' + item.id | t">
                                            @for (sub of item.subpages; track sub.id) {
                                                <li>
                                                    <a [routerLink]="['/admin', item.id, sub.id]" [attr.aria-current]="sub.id === subpage()?.id ? 'page' : null"
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

                <div class="admin-main">
                    @if (overview()) {
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
                        <div class="admin-panel">
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
        // The breadcrumbs' group: its heading on the overview (no router anchor scrolling).
        effect(() => {
            const fragment = this.fragment();
            if (fragment && this.overview())
                afterNextRender(() => this.host.querySelector(`#${CSS.escape(fragment)}`)?.scrollIntoView?.({ block: 'start' }), { injector: this.injector });
        });
    }
}
