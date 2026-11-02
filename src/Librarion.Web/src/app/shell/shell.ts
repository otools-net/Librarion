import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { MessageKey, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { Media } from '../core/media';
import { ADMIN_RIGHTS, Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { ThemeService } from '../core/theme.service';
import { Icon, IconName } from '../shared/icon';
import { Logo } from '../shared/logo';
import { TaskPanel } from '../shared/task-panel';

interface NavItem {
    label: MessageKey;
    icon: IconName;
    /** Null: shown, not reachable yet (a later stage brings it). */
    link: string | null;
}

/** What is open over the page: the navigation (phones), the account menu or the library switcher. */
type Popup = 'nav' | 'account' | 'library';

/**
 * The app's frame (agents/docs/features/shell.md): a top bar (library switcher, search, account menu), the library's
 * navigation (a sticky sidebar on wider screens, a sheet from the bottom on phones, ≤ 640 px) and the page. The
 * administration is a page of its own (D-5.9): there the sidebar, switcher and search give way to a way back.
 */
@Component({
    selector: 'lib-shell',
    imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe, Icon, Logo, TaskPanel],
    templateUrl: './shell.html',
    host: {
        class: 'shell', '[class.admin-frame]': 'inAdmin()',
        '(document:keydown.escape)': 'close()', '(document:pointerdown)': 'pointerDown($event)',
    },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Shell {
    protected readonly theme = inject(ThemeService);
    protected readonly sessions = inject(SessionService);
    protected readonly libraries = inject(LibraryContext);
    private readonly router = inject(Router);
    private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

    protected readonly popup = signal<Popup | null>(null);
    private readonly url = signal(this.router.url);
    /** The last library page, where the administration's back arrow returns. */
    private lastLibraryUrl = '/';
    /** The account and tenant the libraries were loaded for. */
    private loadedFor = '';
    protected readonly inAdmin = computed(() => /^\/admin(?:[/?#]|$)/.test(this.url()));
    private readonly phone = inject(Media).phone;
    /** Phones, in a section of the administration: the arrow goes back to the list of sections, not the library. */
    protected readonly backToSections = computed(() => this.phone() && /^\/admin\/[^/?#]+/.test(this.url()) && !this.inLibrary());
    /** Phones, on a library's pages: the arrow goes back to the list of libraries. */
    protected readonly inLibrary = computed(() => this.phone() && /^\/admin\/libraries\/\d+/.test(this.url()));

    protected readonly nav: NavItem[] = [
        { label: 'shell.nav.home', icon: 'home', link: '/' },
        { label: 'shell.nav.library', icon: 'library', link: null },
        { label: 'shell.nav.authors', icon: 'author', link: null },
        { label: 'shell.nav.series', icon: 'series', link: null },
        { label: 'shell.nav.tags', icon: 'tag', link: null },
    ];

    protected readonly canAdmin = computed(() => {
        this.sessions.session();
        return this.sessions.canAny(...ADMIN_RIGHTS);
    });

    /** The account's name as shown, and its initial in the avatar. */
    protected readonly userName = computed(() => {
        const user = this.sessions.user();
        return user?.displayName || user?.userName || '';
    });
    protected readonly initial = computed(() => this.userName().charAt(0).toUpperCase() || '?');

    /** Server administrators switch tenants from the account menu when there are several. */
    protected readonly tenants = computed(() => {
        const session = this.sessions.session();
        return session?.user?.isServerAdmin && session.tenants.length > 1 ? session.tenants : [];
    });

    constructor() {
        this.router.events.pipe(filter(event => event instanceof NavigationEnd)).subscribe(event => {
            this.url.set(event.urlAfterRedirects);
            if (!this.inAdmin())
                this.lastLibraryUrl = event.urlAfterRedirects;
            this.close();
        });
        // The libraries of the session's tenant: again after a switch of tenant or account.
        effect(() => {
            const session = this.sessions.session();
            const key = `${session?.user?.id}/${session?.tenant?.id}`;
            untracked(() => {
                if (key !== this.loadedFor && this.sessions.canAny(Rights.browse, Rights.manageLibraries, Rights.manageTenants)) {
                    this.loadedFor = key;
                    void this.libraries.load();
                }
            });
        });
    }

    protected toggle(popup: Popup): void {
        this.popup.update(open => open === popup ? null : popup);
        // The chosen library's chip may be past the end of its line (SFE's chip-in-view rule).
        if (this.popup() === 'nav')
            setTimeout(() => this.host.querySelector('.library-chips .active')?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }));
    }

    protected close(): void {
        this.popup.set(null);
    }

    /** A press outside the open menu closes it (SFE: `document:pointerdown`, so a scroll start counts too). */
    protected pointerDown(event: PointerEvent): void {
        const open = this.popup();
        if (open === 'account' || open === 'library') {
            const anchor = this.host.querySelector(`[data-popup="${open}"]`);
            if (!anchor?.contains(event.target as Node))
                this.close();
        }
    }

    protected chooseLibrary(id: number): void {
        this.libraries.choose(id);
        this.close();
    }

    protected async switchTenant(id: number): Promise<void> {
        this.close();
        await this.sessions.switchTenant(id);
        void this.router.navigateByUrl('/');
    }

    protected back(): void {
        void this.router.navigateByUrl(this.inLibrary() ? '/admin/libraries' : this.backToSections() ? '/admin' : this.lastLibraryUrl);
    }

    protected async signOut(): Promise<void> {
        this.close();
        await this.sessions.signOut();
        void this.router.navigate(['/login']);
    }
}
