import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { valueOf } from '../core/errors';
import { MessageKey, TranslatePipe } from '../core/i18n.service';
import { ADMIN_RIGHTS } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ThemeService } from '../core/theme.service';
import { Icon, IconName } from '../shared/icon';
import { Logo } from '../shared/logo';

interface NavItem {
    label: MessageKey;
    icon: IconName;
    /** Null: shown, not reachable yet (a later stage brings it). */
    link: string | null;
}

/**
 * The app's frame: top bar (search, tenant switcher for server administrators, theme, account), navigation (a sidebar
 * on wider screens, a drawer from the left on phones, ≤ 640 px) and the page. Phone rules:
 * agents/docs/development/frontend.md.
 */
@Component({
    selector: 'lib-shell',
    imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe, Icon, Logo],
    templateUrl: './shell.html',
    host: { class: 'shell', '(document:keydown.escape)': 'closeDrawer()' },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Shell {
    protected readonly theme = inject(ThemeService);
    protected readonly sessions = inject(SessionService);
    private readonly router = inject(Router);
    protected readonly drawerOpen = signal(false);

    protected readonly nav = computed<NavItem[]>(() => {
        this.sessions.session();
        const items: NavItem[] = [
            { label: 'shell.nav.home', icon: 'home', link: '/' },
            { label: 'shell.nav.library', icon: 'library', link: null },
            { label: 'shell.nav.authors', icon: 'author', link: null },
            { label: 'shell.nav.series', icon: 'series', link: null },
            { label: 'shell.nav.tags', icon: 'tag', link: null },
        ];
        if (this.sessions.canAny(...ADMIN_RIGHTS))
            items.push({ label: 'shell.nav.admin', icon: 'settings', link: '/admin' });
        return items;
    });

    /** Server administrators switch tenants from the top bar when there are several. */
    protected readonly tenants = computed(() => {
        const session = this.sessions.session();
        return session?.user?.isServerAdmin && session.tenants.length > 1 ? session.tenants : [];
    });

    protected readonly valueOf = valueOf;

    constructor() {
        this.router.events.pipe(filter(event => event instanceof NavigationEnd)).subscribe(() => this.closeDrawer());
    }

    protected toggleDrawer(): void {
        this.drawerOpen.update(open => !open);
    }

    protected closeDrawer(): void {
        this.drawerOpen.set(false);
    }

    protected async switchTenant(id: number): Promise<void> {
        await this.sessions.switchTenant(id);
        void this.router.navigateByUrl('/');
    }
}
