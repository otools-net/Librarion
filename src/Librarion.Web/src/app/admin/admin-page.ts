import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MessageKey, TranslatePipe } from '../core/i18n.service';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';

interface AdminSection {
    path: string;
    label: MessageKey;
    /** Any of them shows the section. */
    rights: string[];
}

/** The sections of the administration, each shown to whoever has one of its rights (SFE's admin-sections). */
export const ADMIN_SECTIONS: AdminSection[] = [
    { path: 'users', label: 'admin.users', rights: [Rights.manageUsers, Rights.manageAllUsers] },
    { path: 'groups', label: 'admin.groups', rights: [Rights.manageUsers, Rights.manageTenants] },
    { path: 'libraries', label: 'admin.libraries', rights: [Rights.manageLibraries, Rights.manageTenants] },
    { path: 'tenants', label: 'admin.tenants', rights: [Rights.manageTenants] },
    { path: 'settings', label: 'admin.settings', rights: [Rights.serverSettings, Rights.tenantSettings, Rights.librarySettings] },
    { path: 'security', label: 'admin.security', rights: [Rights.tenantSecurityLog, Rights.serverSecurityLog] },
];

/** Administration: a row of sections (scrolls sideways on phones), the chosen one below. */
@Component({
    selector: 'lib-admin-page',
    imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <h1>{{ 'admin.title' | t }}</h1>
        <nav class="tabs" [attr.aria-label]="'admin.sections' | t">
            @for (section of sections(); track section.path) {
                <a class="tab" [routerLink]="section.path" routerLinkActive="active" [title]="section.label | t">{{ section.label | t }}</a>
            }
        </nav>
        <router-outlet />
    `,
})
export class AdminPage implements OnInit {
    private readonly sessions = inject(SessionService);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);
    protected readonly sections = computed(() => {
        this.sessions.session();
        return ADMIN_SECTIONS.filter(section => this.sessions.canAny(...section.rights));
    });

    /** `/admin` alone opens the first section the session may use. */
    ngOnInit(): void {
        const first = this.sections()[0];
        if (!this.route.firstChild && first)
            void this.router.navigate([first.path], { relativeTo: this.route, replaceUrl: true });
    }
}
