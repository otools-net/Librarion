import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { MessageKey, TranslatePipe } from '../core/i18n.service';
import { ThemeService } from '../core/theme.service';
import { Icon, IconName, IconSprite } from '../shared/icon';

interface NavItem {
    label: MessageKey;
    icon: IconName;
    /** Null: shown, not reachable yet (a later stage brings it). */
    link: string | null;
}

/**
 * The app's frame: top bar, navigation (a sidebar on wider screens, a drawer from the left on phones, ≤ 640 px) and
 * the page. Phone rules: agents/docs/development/frontend.md.
 */
@Component({
    selector: 'lib-shell',
    imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe, Icon, IconSprite],
    templateUrl: './shell.html',
    host: { class: 'shell', '(document:keydown.escape)': 'closeDrawer()' },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Shell {
    protected readonly theme = inject(ThemeService);
    protected readonly drawerOpen = signal(false);

    protected readonly nav: NavItem[] = [
        { label: 'shell.nav.home', icon: 'home', link: '/' },
        { label: 'shell.nav.library', icon: 'library', link: null },
        { label: 'shell.nav.authors', icon: 'author', link: null },
        { label: 'shell.nav.series', icon: 'series', link: null },
        { label: 'shell.nav.tags', icon: 'tag', link: null },
    ];

    constructor() {
        inject(Router).events.pipe(filter(event => event instanceof NavigationEnd)).subscribe(() => this.closeDrawer());
    }

    protected toggleDrawer(): void {
        this.drawerOpen.update(open => !open);
    }

    protected closeDrawer(): void {
        this.drawerOpen.set(false);
    }
}
