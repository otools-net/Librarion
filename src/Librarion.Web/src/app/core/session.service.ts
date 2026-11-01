import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Api } from './api.service';
import { SessionInfo } from './models';
import { ThemeService } from './theme.service';

/**
 * Who is signed in, in which tenant, with which rights (`api/session`), as SFE's ConfigService: loaded once, reloaded
 * after signing in or out or switching tenants. `can(right)` decides what pages and buttons show; the server checks
 * every request anyway.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
    private readonly api = inject(Api);
    private readonly theme = inject(ThemeService);
    private readonly current = signal<SessionInfo | null>(null);
    private loading: Promise<SessionInfo> | null = null;

    readonly session = this.current.asReadonly();
    readonly user = computed(() => this.current()?.user ?? null);
    private readonly rights = computed(() => new Set(this.current()?.rights ?? []));

    /** The session, asked for once. */
    load(): Promise<SessionInfo> {
        const loaded = this.current();
        if (loaded)
            return Promise.resolve(loaded);
        return this.loading ??= this.api.session().then(session => this.set(session), error => {
            this.loading = null;
            throw error;
        });
    }

    /** A new session (after signing in or out, a tenant switch): what depends on it reads it again. */
    set(session: SessionInfo): SessionInfo {
        this.loading = null;
        this.current.set(session);
        const theme = session.preferences['ui.theme'];
        if (theme === 'light' || theme === 'dark')
            this.theme.choose(theme);
        return session;
    }

    /** Forgets the session: the next `load` asks the server. */
    clear(): void {
        this.loading = null;
        this.current.set(null);
    }

    can(right: string): boolean {
        return this.rights().has(right);
    }

    canAny(...rights: string[]): boolean {
        return rights.some(right => this.rights().has(right));
    }

    async signOut(): Promise<void> {
        this.set(await this.api.signOut());
    }

    async switchTenant(tenantId: number): Promise<void> {
        this.set(await this.api.switchTenant(tenantId));
    }
}

/** The app's pages need an account: the setup page first on a new server, else the sign-in page. */
export const signedIn: CanActivateFn = async (_route, state) => {
    const sessions = inject(SessionService);
    const router = inject(Router);
    try {
        const session = await sessions.load();
        if (session.setupRequired)
            return router.createUrlTree(['/setup']);
        return session.user ? true : router.createUrlTree(['/login'], { queryParams: { return: state.url } });
    } catch {
        return router.createUrlTree(['/login'], { queryParams: { return: state.url } });
    }
};

/** A page that needs one of `rights`: home without it. */
export function needsRight(...rights: string[]): CanActivateFn {
    return () => inject(SessionService).canAny(...rights) || inject(Router).createUrlTree(['/']);
}
